//! Plain-text books: encoding detection, paragraph reconstruction and chapter detection.
//!
//! The structure detection is also used for EPUBs that are really text files in XHTML clothes
//! (many public-domain Chinese books: one huge `<p>` per few hundred lines, hard-wrapped, with
//! "　　" starting each paragraph and no table of contents).

use std::collections::HashSet;
use std::sync::LazyLock;

use encoding_rs::Encoding;
use regex::Regex;

use crate::dom::{Element, Node};
use crate::text::{guess_language, is_cjk, is_wide, width};

pub struct TextBook {
    pub title: Option<String>,
    pub author: Option<String>,
    pub language: &'static str,
    pub chapters: Vec<Chapter>,
}

#[derive(Debug, Default, PartialEq)]
pub struct Chapter {
    pub title: Option<String>,
    pub paragraphs: Vec<String>,
    /// A later part of a chapter that was too long for one section.
    pub continued: bool,
}

/// Chapters longer than this are cut into parts at paragraph boundaries, so a WebView never has
/// to lay out a whole novel at once.
const MAX_SECTION_CHARS: usize = 60_000;
const PART_CHARS: usize = 30_000;

/// The paragraph text used for a scene break ("* * *", "————").
pub const SCENE_BREAK: &str = "* * *";

/// Decodes a text file: BOM, then UTF-8, then a statistical guess (GBK/Big5/Shift_JIS…).
pub fn decode(bytes: &[u8]) -> String {
    decode_as(bytes, None).text
}

pub struct Decoded {
    pub text: String,
    pub encoding: &'static Encoding,
    /// Bytes that weren't text in that encoding, each now a U+FFFD.
    pub errors: usize,
}

/// Decodes in the given encoding, or the one `decode` would pick.
pub fn decode_as(bytes: &[u8], forced: Option<&'static Encoding>) -> Decoded {
    let (text, encoding) = match forced {
        Some(enc) => (enc.decode_with_bom_removal(bytes).0.into_owned(), enc),
        None => {
            if let Some((enc, bom_len)) = Encoding::for_bom(bytes) {
                (enc.decode_without_bom_handling(&bytes[bom_len..]).0.into_owned(), enc)
            } else if let Ok(s) = std::str::from_utf8(bytes) {
                (s.to_string(), encoding_rs::UTF_8)
            } else {
                let mut detector = chardetng::EncodingDetector::new(chardetng::Iso2022JpDetection::Deny);
                detector.feed(bytes, true);
                let enc = detector.guess(None, chardetng::Utf8Detection::Allow);
                (enc.decode(bytes).0.into_owned(), enc)
            }
        }
    };
    let errors = text.chars().filter(|&c| c == '\u{FFFD}').count();
    Decoded { text, encoding, errors }
}

/// "GBK", "UTF-8", "GB18030", "Big5"…
pub fn encoding_name(enc: &'static Encoding) -> String {
    if enc == encoding_rs::GB18030 { "GB18030".into() } else { enc.name().to_string() }
}

pub fn encoding_for(name: &str) -> Option<&'static Encoding> {
    Encoding::for_label(name.trim().as_bytes())
}

/// The chapter rule shown when none was written: what the importer looks for in Chinese books.
/// It also knows 序章, 楔子, "Chapter 3" and the like, which a written rule replaces.
pub const DEFAULT_RULE: &str = "^第[0-9零一二三四五六七八九十百千万两]+[章回节卷部篇集]";

/// What starts a chapter: the importer's own idea, or a rule the reader wrote.
#[derive(Default)]
pub struct Headings(Option<Regex>);

impl Headings {
    /// A rule the reader wrote; the default one, or none, means the importer's own.
    pub fn rule(rule: Option<&str>) -> Result<Headings, regex::Error> {
        match rule.map(str::trim).filter(|r| !r.is_empty() && *r != DEFAULT_RULE) {
            Some(r) => Ok(Headings(Some(Regex::new(r)?))),
            None => Ok(Headings(None)),
        }
    }

    fn is(&self, line: &str) -> bool {
        match &self.0 {
            Some(r) => line.chars().count() <= 60 && r.is_match(line),
            None => is_heading(line),
        }
    }

    fn is_bare(&self, line: &str) -> bool {
        self.0.is_none() && is_bare_heading(line)
    }
}

const CJK_NUMBER: &str = r"[\s　]*[0-9０-９零〇一二三四五六七八九十百千万两壹贰叁肆伍陆柒捌玖拾佰仟萬]+[\s　]*";
const LATIN_NUMBER: &str = r"(?i:[0-9]+|[ivxlcdm]+|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|thirteen|fourteen|fifteen|sixteen|seventeen|eighteen|nineteen|twenty|thirty|forty|fifty|sixty|seventy|eighty|ninety|hundred|first|second|third|fourth|fifth|sixth|seventh|eighth|ninth|tenth|last)";

/// Optional volume / section prefix before a CJK chapter heading:
/// "正文 第1章 …", "正文卷 第001章 …", "第一卷 农家子弟 第1章 …", "VIP卷 第100章 …".
static VOLUME_PREFIX: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(
        r"^(?:正文卷?|VIP卷|vip卷|公众卷|免费卷|外篇|番外卷?|卷[\s　]*[0-9０-９零〇一二三四五六七八九十百千万两上中下终]+|第[\s　]*[0-9０-９零〇一二三四五六七八九十百千万两]+[\s　]*[卷部篇集])(?:[\s　：:·\-—]+[^第\s　][^\s　]{0,23})?[\s　]+",
    )
    .unwrap()
});
static CJK_HEADING: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(&format!(
        r"^第{CJK_NUMBER}[章回节節卷部篇集幕话話](?:[\s　：:．.、，,·\-—–_~～!！?？(（\[【『「《\x22“'‘/|]|$)"
    ))
    .unwrap()
});
/// "第1章山村少年" (no space or punctuation after 章/回/节/话). Checked against prose starters.
static CJK_HEADING_COMPACT: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(&format!(r"^第{CJK_NUMBER}[章回节節话話]([^\s　])")).unwrap()
});
static CJK_BARE: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(&format!(r"^第{CJK_NUMBER}[章回节節卷部篇集幕话話]$")).unwrap());
static NAMED_HEADING: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r"^(序章|序言|序幕|序|楔子|引子|引言|前言|后记|後記|尾声|尾聲|终章|終章|大结局|大結局|番外|外传|外傳|附录|附錄|跋|上架感言|完本感言)(?:[\s　]*[0-9０-９零〇一二三四五六七八九十百千万两]+)?([\s　：:．.、·\-—（(【\[].*)?$").unwrap()
});
/// "CHAPTER XII.", "Chapter 3: The Party", "BOOK ONE — THE RIVER", "Prologue". A sentence that
/// merely starts with "Chapter one of…" doesn't match: anything after the number must follow a
/// separator or start with a capital.
static LATIN_HEADING: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(&format!(
        r"^(?:(?i:chapter|book|part|volume|act|canto)\s+{LATIN_NUMBER}(?:[\s-]{LATIN_NUMBER})*|(?i:prologue|epilogue|preface|introduction|afterword|foreword|appendix))\.?(?:\s*[:.\-—–]\s*\S.*|\s+\p{{Lu}}.*)?$"
    ))
    .unwrap()
});
static LATIN_BARE: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(&format!(r"^(?i:chapter|book|part|volume)\s+{LATIN_NUMBER}(?:[\s-]{LATIN_NUMBER})*\.?$")).unwrap()
});
/// Bare numeric headings ("001 联盟远征军", "1. 山猫流浪记", "251、大梦学宫", "【001】标题"),
/// enabled only by whole-book detection when a TXT numbers chapters without "第…章".
static BARE_NUMERIC_HEADING: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r"^(?:[【『\[\(（][\s　]*([0-9０-９]{1,4})[\s　]*[】』\]\)）][\s　]*|([0-9０-９]{1,4})(?:[\s　]*[．.、:：\-—][\s　]*|[\s　]+))(\S.*)$")
        .unwrap()
});

/// Strips balanced outer brackets and leading decoration / volume prefixes so headings like
/// "正文 第1章 …", "【第1章 …】", "☆、第1章 …" or "=== 第1章 … ===" match cleanly.
fn unwrap_heading_candidate(line: &str) -> &str {
    let mut s = trim(line);
    for (open, close) in [('【', '】'), ('『', '』'), ('「', '」'), ('[', ']'), ('（', '）'), ('(', ')'), ('《', '》')] {
        if let Some(inner) = s.strip_prefix(open).and_then(|rest| rest.strip_suffix(close)) {
            s = trim(inner);
            break;
        }
    }
    let stripped = s.trim_start_matches(|c: char| {
        matches!(c, '☆' | '★' | '●' | '◆' | '◇' | '※' | '•' | '·' | '=' | '-' | '—' | '*' | '#' | '~' | '～')
    });
    if stripped.len() < s.len() {
        s = trim(stripped.trim_start_matches('、'));
    }
    if let Some(m) = VOLUME_PREFIX.find(s)
        && s[m.end()..].starts_with('第')
    {
        s = &s[m.end()..];
    }
    s
}

/// Characters that immediately follow "第X章" in prose ("第三章说的是…", "第一章里…") rather than
/// starting a compact title without a space ("第1章山村少年").
const PROSE_AFTER_CHAPTER: &str = "说写讲里中的是有会在就把被给让到了也都还很太真不没可但而和与才便又再只却并若如虽因为从向对于以之其这那他她它我你们过完后前末尾头";

pub fn is_heading(line: &str) -> bool {
    let n = line.chars().count();
    if n == 0 || n > 70 {
        return false;
    }
    // Dialogue lines starting with quotes are prose, not chapter headings.
    if line.starts_with(['“', '”', '"', '\'']) {
        return false;
    }
    let candidate = unwrap_heading_candidate(line);
    if candidate.is_empty() {
        return false;
    }
    if CJK_HEADING.is_match(candidate) || NAMED_HEADING.is_match(candidate) {
        // "第三章说的是……。" is a sentence, not a heading; web-novel titles often contain ！ / ？.
        return !candidate.contains('。');
    }
    if let Some(c) = CJK_HEADING_COMPACT.captures(candidate) {
        let next = c[1].chars().next().unwrap_or(' ');
        return n <= 60
            && !candidate.contains('。')
            && !candidate.ends_with(['，', '；', '：', '、', ',', ';', ':'])
            && !PROSE_AFTER_CHAPTER.contains(next);
    }
    n <= 60 && LATIN_HEADING.is_match(candidate)
}

/// A heading with only its number ("第一章", "CHAPTER I."), whose name may be on the next line.
fn is_bare_heading(line: &str) -> bool {
    let candidate = unwrap_heading_candidate(line);
    CJK_BARE.is_match(candidate) || LATIN_BARE.is_match(candidate)
}

static COUPLET_HEADING: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(&format!(r"^(第{CJK_NUMBER}回)[\s　]+(\p{{Han}}{{5,9}})[\s　]+(\p{{Han}}{{5,9}})[\s　]+(\S.*)$")).unwrap()
});

static COUPLET: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"^(\p{Han}{5,9})[\s　]+(\p{Han}{5,9})$").unwrap());

/// "賈雨村夤緣复舊職　林黛玉拋父進京都": the name of a chapter in a classical novel, which may
/// be followed directly by the chapter's text.
fn is_couplet(line: &str) -> bool {
    COUPLET.captures(line).is_some_and(|c| c[1].chars().count() == c[2].chars().count())
}

/// Splits text that ran on into a heading line off the heading. Chapter titles of classical
/// novels are couplets of two equal halves ("第三十一回　撕扇子作千金一笑　因麒麟伏白首雙星"), so
/// anything after the second half is the chapter's first words.
fn split_heading(line: &str) -> (String, Option<String>) {
    if let Some(c) = COUPLET_HEADING.captures(line)
        && c[2].chars().count() == c[3].chars().count() {
            return (format!("{} {} {}", collapse_inner(&c[1]), &c[2], &c[3]), Some(c[4].to_string()));
        }
    (collapse_inner(line), None)
}

fn is_decoration(line: &str) -> bool {
    line.chars().count() >= 3
        && line.chars().all(|c| matches!(c, '─' | '—' | '-' | '=' | '*' | '~' | '_' | '·' | '－' | '＝' | '＊' | '※' | ' ' | '　'))
}

/// `Some(true)` for "*** START OF THE PROJECT GUTENBERG EBOOK …", `Some(false)` for its "END".
fn is_gutenberg_marker(line: &str) -> Option<bool> {
    let upper = line.trim_start_matches(['*', ' ']).to_ascii_uppercase();
    if !upper.contains("PROJECT GUTENBERG") {
        return None;
    }
    if upper.starts_with("START OF") {
        Some(true)
    } else if upper.starts_with("END OF") {
        Some(false)
    } else {
        None
    }
}

fn trim(line: &str) -> &str {
    line.trim_matches(|c: char| c.is_whitespace() || c == '\u{FEFF}')
}

fn indent(line: &str) -> usize {
    line.chars().take_while(|c| c.is_whitespace()).map(|c| if c == '\u{3000}' { 2 } else { 1 }).sum()
}

/// How the lines of a file are laid out.
struct Layout {
    /// The width hard-wrapped lines were wrapped at, if the file is hard-wrapped at all. Files
    /// with one paragraph per line have wildly varying line widths and no such width.
    wrap: Option<usize>,
    /// The indent of continuation lines; paragraphs start indented further.
    base_indent: usize,
}

impl Layout {
    fn of(lines: &[&str]) -> Layout {
        let body: Vec<&str> = lines.iter().copied().filter(|l| !trim(l).is_empty()).collect();
        if body.len() < 20 {
            return Layout { wrap: None, base_indent: 0 };
        }
        let mut widths: Vec<usize> = body.iter().map(|l| width(l.trim_end())).collect();
        widths.sort_unstable();
        let w = widths[widths.len() * 9 / 10];
        let full = widths.iter().filter(|&&x| x * 100 >= w * 85).count();
        let mut indents: Vec<usize> = body.iter().map(|l| indent(l)).collect();
        indents.sort_unstable();
        // When (nearly) every line starts with the same indent, every line starts a paragraph.
        let mut common = (0, 0);
        for run in indents.chunk_by(|a, b| a == b) {
            if run.len() > common.1 {
                common = (run[0], run.len());
            }
        }
        let every_line_indented = common.0 > 0 && common.1 * 10 >= body.len() * 8;
        // Hard wrapping happens at 60–80 columns; most lines then sit near the maximum.
        let wrapped = w <= 200 && full * 100 >= body.len() * 35 && !every_line_indented;
        Layout { wrap: wrapped.then_some(w), base_indent: indents[indents.len() / 20] }
    }

    fn is_full(&self, raw: &str) -> bool {
        self.wrap.is_some_and(|w| width(raw.trim_end()) * 10 >= w * 8)
    }
}

/// A text file read one way: in an encoding, with a chapter rule.
pub struct Analysis {
    pub decoded: Decoded,
    pub book: TextBook,
    /// Each chapter's title, length in characters with its parts put back together, and the
    /// section it starts.
    pub outline: Vec<(Option<String>, usize, usize)>,
    /// The chapter in `outline` that is far longer than the rest: a heading the rule missed.
    pub long: Option<usize>,
    /// Why the result may be wrong, to point the reader at 编码和目录.
    pub doubt: Option<String>,
}

pub fn analyze(bytes: &[u8], encoding: Option<&'static Encoding>, headings: &Headings) -> Analysis {
    let decoded = decode_as(bytes, encoding);
    let book = structure_with(&decoded.text, headings);
    let mut outline: Vec<(Option<String>, usize, usize)> = Vec::new();
    for (i, ch) in book.chapters.iter().enumerate() {
        let n = ch.paragraphs.iter().map(|p| p.chars().count()).sum::<usize>();
        match outline.last_mut() {
            Some(last) if ch.continued => last.1 += n,
            _ => outline.push((ch.title.clone(), n, i)),
        }
    }
    let long = long_chapter(&outline);
    let chars = decoded.text.chars().count();
    let titled = outline.iter().filter(|(t, _, _)| t.is_some()).count();
    let doubt = if decoded.errors >= 20 || decoded.errors * 1000 > chars {
        Some(crate::i18n::tr("有些字没认出来，可能是编码不对", "Some characters weren't recognized; the encoding may be wrong"))
    } else if chars >= 50_000 && titled <= 1 {
        Some(crate::i18n::tr("没认出章节，目录可能不对", "No chapters were found; the contents may be wrong"))
    } else {
        long.map(|_| crate::i18n::tr("有一章长得不正常，可能漏了章节", "One chapter is unusually long; some chapters may be missing"))
    };
    Analysis { decoded, book, outline, long, doubt }
}

/// A chapter far longer than the usual one, suggesting a heading the rule missed.
/// Zero-char volume dividers are ignored when taking the median, and normal web-novel big
/// chapters (~10k chars) don't trigger a false alarm.
fn long_chapter(outline: &[(Option<String>, usize, usize)]) -> Option<usize> {
    if outline.len() < 5 {
        return None;
    }
    let mut sizes: Vec<usize> = outline.iter().map(|(_, n, _)| *n).filter(|&n| n > 0).collect();
    if sizes.len() < 5 {
        return None;
    }
    sizes.sort_unstable();
    let median = sizes[sizes.len() / 2];
    let (i, &(_, n, _)) = outline.iter().enumerate().max_by_key(|(_, (_, n, _))| *n)?;
    ((n >= 18_000 && n >= median * 4) || n >= 80_000).then_some(i)
}

/// Finds the title, author, paragraphs and chapters of a text file.
pub fn structure(text: &str) -> TextBook {
    structure_with(text, &Headings::default())
}

pub fn structure_with(text: &str, headings: &Headings) -> TextBook {
    let text = text.replace("\r\n", "\n").replace('\r', "\n");
    let lines: Vec<&str> = text.split('\n').collect();
    let (title, author, start) = front_matter(&lines, headings);
    let mut chapters = chapters_from(&lines, start, headings);
    if chapters.len() == 1 && chapters[0].title.is_none() {
        chapters[0].title = title.clone();
    }
    TextBook { title, author, language: guess_language(&text), chapters }
}

/// Paragraphs and chapters only, for text whose title is known from elsewhere.
pub fn chapters(text: &str) -> Vec<Chapter> {
    let text = text.replace("\r\n", "\n").replace('\r', "\n");
    let lines: Vec<&str> = text.split('\n').collect();
    chapters_from(&lines, 0, &Headings::default())
}

/// Parses an ASCII or fullwidth decimal integer ("001", "２５０").
fn parse_digits(s: &str) -> Option<u32> {
    let mut val: u32 = 0;
    let mut any = false;
    for c in s.chars() {
        let d = match c {
            '0'..='9' => c as u32 - '0' as u32,
            '０'..='９' => c as u32 - '０' as u32,
            _ => return None,
        };
        val = val.checked_mul(10)?.checked_add(d)?;
        any = true;
    }
    any.then_some(val)
}

/// Units after a number that mark a date, quantity or list item rather than a chapter title
/// ("2026年…", "100元…", "3个人…").
const QUANTITY_AFTER_NUMBER: &str = "年月日号点分秒元块万亿千百个名人次岁米斤克%％";

fn bare_numeric_chapter(line: &str) -> Option<u32> {
    let n = line.chars().count();
    if n < 2 || n > 60 || line.contains('。') || line.starts_with(['“', '”', '"', '\'']) {
        return None;
    }
    let c = BARE_NUMERIC_HEADING.captures(line)?;
    let num_str = c.get(1).or_else(|| c.get(2))?.as_str();
    let num = parse_digits(num_str)?;
    if num == 0 || num > 9999 {
        return None;
    }
    let title = trim(&c[3]);
    if title.is_empty() || title.ends_with(['，', '；', '：', ',', ';', ':']) {
        return None;
    }
    let first = title.chars().next().unwrap_or(' ');
    if QUANTITY_AFTER_NUMBER.contains(first) {
        return None;
    }
    Some(num)
}

/// Detects bare numeric chapter headings ("001 标题", "1. 标题", "251、标题") across the whole
/// book when the standard auto rule left large unsplit stretches. Short numbered lists in prose
/// are ignored by requiring sequential numbering separated by real chapter bodies.
fn detect_numeric_headings(lines: &[&str], start: usize) -> HashSet<usize> {
    let mut total_chars = 0usize;
    let mut std_count = 0usize;
    let mut last_std_offset = 0usize;
    let mut max_std_gap = 0usize;
    let mut candidates: Vec<(usize, u32, usize)> = Vec::new();

    for (i, raw) in lines.iter().enumerate().skip(start) {
        let line = trim(raw);
        if line.is_empty() {
            continue;
        }
        let len = line.chars().count();
        if is_heading(line) {
            std_count += 1;
            max_std_gap = max_std_gap.max(total_chars.saturating_sub(last_std_offset));
            last_std_offset = total_chars;
        } else if let Some(num) = bare_numeric_chapter(line) {
            candidates.push((i, num, total_chars));
        }
        total_chars += len;
    }
    max_std_gap = max_std_gap.max(total_chars.saturating_sub(last_std_offset));

    let needs_fallback = total_chars >= 3_000
        && (std_count == 0 || total_chars / std_count.max(1) >= 20_000 || max_std_gap >= 40_000);
    if !needs_fallback || candidates.len() < 5 {
        return HashSet::new();
    }

    // Keep candidates that have meaningful spacing (filtering out tight inline numbered lists
    // while still allowing a leading TOC list which `drop_contents_lists` will remove).
    let mut body_gaps: Vec<usize> = Vec::with_capacity(candidates.len());
    let mut sequential = 0usize;
    for w in candidates.windows(2) {
        let (_, n0, o0) = w[0];
        let (_, n1, o1) = w[1];
        let gap = o1.saturating_sub(o0);
        if gap >= 50 {
            body_gaps.push(gap);
        }
        if n1 > n0 && n1 <= n0 + 20 {
            sequential += 1;
        }
    }
    if body_gaps.len() < 4 || sequential < 4 || sequential * 2 < candidates.len().saturating_sub(1) {
        return HashSet::new();
    }
    body_gaps.sort_unstable();
    let median_gap = body_gaps[body_gaps.len() / 2];
    if median_gap < 300 {
        return HashSet::new();
    }

    // Drop isolated inline numbered-list items (too close to neighbours with tiny gap), while
    // keeping actual chapters spaced apart (or runs of 3+ empty TOC lines).
    let mut accepted = HashSet::with_capacity(candidates.len());
    for (idx, &(line_i, _, off)) in candidates.iter().enumerate() {
        let prev_gap = idx.checked_sub(1).map(|p| off.saturating_sub(candidates[p].2)).unwrap_or(usize::MAX);
        let next_gap = candidates.get(idx + 1).map(|n| n.2.saturating_sub(off)).unwrap_or(usize::MAX);
        if prev_gap >= 150 || next_gap >= 150 {
            accepted.insert(line_i);
        }
    }
    if accepted.len() >= 5 { accepted } else { HashSet::new() }
}

fn chapters_from(lines: &[&str], start: usize, headings: &Headings) -> Vec<Chapter> {
    let layout = Layout::of(lines);
    let extra_headings = if headings.0.is_none() { detect_numeric_headings(lines, start) } else { HashSet::new() };

    let mut chapters = vec![Chapter::default()];
    let mut para: Option<String> = None;
    let mut prev_full = false;
    // The chapter just started with a bare "第一章"; its name may follow on its own line.
    let mut bare_heading = false;

    fn end_para(para: &mut Option<String>, chapters: &mut [Chapter]) {
        if let Some(p) = para.take() {
            chapters.last_mut().unwrap().paragraphs.push(p);
        }
    }

    for (i, raw) in lines.iter().enumerate().skip(start) {
        let line = trim(raw);
        if line.is_empty() {
            end_para(&mut para, &mut chapters);
            prev_full = false;
            continue;
        }
        if headings.is(line) || extra_headings.contains(&i) {
            end_para(&mut para, &mut chapters);
            let (title, rest) = split_heading(line);
            chapters.push(Chapter { title: Some(title), ..Default::default() });
            // Text that ran on into the heading line continues on the next line.
            prev_full = rest.is_some();
            para = rest;
            bare_heading = !extra_headings.contains(&i) && headings.is_bare(line);
            continue;
        }
        if let Some(starting) = is_gutenberg_marker(line) {
            // Project Gutenberg's licence header and footer become sections of their own.
            end_para(&mut para, &mut chapters);
            if !starting {
                chapters.push(Chapter::default());
            }
            chapters.last_mut().unwrap().paragraphs.push(line.to_string());
            if starting {
                chapters.push(Chapter::default());
            }
            prev_full = false;
            bare_heading = false;
            continue;
        }
        if is_decoration(line) {
            end_para(&mut para, &mut chapters);
            let current = chapters.last_mut().unwrap();
            if current.paragraphs.last().is_some_and(|p| p != SCENE_BREAK) {
                current.paragraphs.push(SCENE_BREAK.into());
            }
            prev_full = false;
            continue;
        }
        if bare_heading {
            bare_heading = false;
            // The name stands alone: the text starts after a blank or a new indented paragraph.
            let stands_alone = lines.get(i + 1).is_none_or(|next| {
                let t = trim(next);
                t.is_empty() || is_decoration(t) || headings.is(t) || indent(next) > layout.base_indent
            });
            let ends_sentence = line.ends_with(['。', '！', '？', '，', '；', '：', '.', '!', '?', ',', ';', ':']);
            if para.is_none() && (stands_alone || is_couplet(line)) && width(line) <= 60 && !ends_sentence {
                if let Some(t) = &mut chapters.last_mut().unwrap().title {
                    t.push(' ');
                    t.push_str(&collapse_inner(line));
                }
                continue;
            }
        }
        let continues = para.is_some() && prev_full && indent(raw) <= layout.base_indent;
        if continues {
            let p = para.as_mut().unwrap();
            let last = p.chars().last().unwrap_or(' ');
            let first = line.chars().next().unwrap_or(' ');
            if !(is_wide(last) || is_wide(first)) {
                p.push(' ');
            }
            p.push_str(line);
        } else {
            end_para(&mut para, &mut chapters);
            para = Some(line.to_string());
        }
        prev_full = layout.is_full(raw);
    }
    end_para(&mut para, &mut chapters);

    chapters.retain(|c| c.title.is_some() || !c.paragraphs.is_empty());
    // A heading repeated after a separator line: keep the one with the text.
    let mut i = 0;
    while i + 1 < chapters.len() {
        if chapters[i].paragraphs.is_empty() && chapters[i].title == chapters[i + 1].title {
            chapters.remove(i);
        } else {
            i += 1;
        }
    }
    for c in &mut chapters {
        if c.paragraphs.last().is_some_and(|p| p == SCENE_BREAK) {
            c.paragraphs.pop();
        }
    }
    drop_contents_lists(&mut chapters);
    chapters.into_iter().flat_map(split_long).collect()
}

const TITLE_LABELS: &[&str] = &["书名", "書名", "Title"];
const AUTHOR_LABELS: &[&str] = &["作者", "著者", "Author"];

/// Strips one or two pairs of matching decorative brackets ("『…』", "【…】", "《…》"…).
fn strip_brackets(mut s: &str) -> &str {
    for _ in 0..2 {
        let before = s;
        s = trim(s);
        for (open, close) in [('『', '』'), ('【', '】'), ('「', '」'), ('《', '》'), ('[', ']'), ('（', '）'), ('(', ')')] {
            if let Some(inner) = s.strip_prefix(open).and_then(|r| r.strip_suffix(close)) {
                s = trim(inner);
                break;
            }
        }
        if s == before {
            break;
        }
    }
    s
}

/// "作者：曹雪芹" / "『作者:曹雪芹』" → "曹雪芹".
fn labelled<'a>(line: &'a str, labels: &[&str]) -> Option<&'a str> {
    let inner = strip_brackets(line);
    let rest = labels.iter().find_map(|l| inner.strip_prefix(l))?;
    let value = trim(rest.trim_start().strip_prefix([':', '：'])?);
    let value = value.trim_end_matches(['』', '】', '」', ']', '）', ')']);
    let value = trim(value);
    (!value.is_empty()).then_some(value)
}

/// Download-site promos, URLs, metadata tags and watermarks commonly pasted at the top of web-novel
/// TXTs; never treat them as a book title.
fn looks_like_junk_title(line: &str) -> bool {
    let s = trim(line);
    if s.is_empty() || is_decoration(s) {
        return true;
    }
    let lower = s.to_ascii_lowercase();
    const URL_TOKENS: &[&str] = &[
        "http://", "https://", "www.", ".com", ".net", ".cn", ".org", ".cc", ".tv", ".la", ".me",
        ".xyz", ".top", ".info", ".txt", ".epub", ".zip", ".rar",
    ];
    if URL_TOKENS.iter().any(|t| lower.contains(t)) {
        return true;
    }
    const PROMO_TOKENS: &[&str] = &[
        "推荐", "下载", "网址", "网站", "整理", "版权", "声明", "仅供", "试读", "更新到", "每天更新",
        "最新章节", "收藏", "访问", "更多精彩", "全本", "完结", "电子书", "书包", "书库", "小说网",
        "阅读网", "免费阅读", "qq群", "微信", "公众号", "作品相关", "内容简介", "内容介绍",
        "书籍简介", "简介", "状态", "文案", "标签", "主角", "配角", "字数", "分类", "评分", "目录",
        "正文", "制作",
    ];
    if PROMO_TOKENS.iter().any(|t| lower.contains(t)) {
        return true;
    }
    if s.contains([':', '：', '/', '\\', '|', '=', '*', '#', '@', '©']) {
        return true;
    }
    // Unparsed metadata lines wrapped in 『…』 / 【…】 / […] / (…) are almost always site headers.
    if s.starts_with(['『', '【', '[', '（', '(']) {
        return true;
    }
    false
}

/// Parses combined title + author headers common in web-novel TXTs and filenames:
/// "《暴风城打工实录》作者：又一个鱼雷", "『神农道君/作者:神威校尉』", "书名：三体 作者：刘慈欣".
pub fn parse_title_author(raw: &str) -> Option<(String, Option<String>)> {
    let inner = strip_brackets(raw);
    if inner.is_empty() || inner.chars().count() > 80 {
        return None;
    }
    let split = ["作者", "著者"].iter().find_map(|label| {
        let pos = inner.find(label)?;
        let after_label = &inner[pos + label.len()..];
        let after_colon = after_label.trim_start().strip_prefix([':', '：'])?;
        Some((&inner[..pos], after_colon))
    })?;

    let mut title_part = trim(split.0);
    // Drop separator characters between title and "作者：" ("/", "|", "（", "-", spaces).
    title_part = trim(title_part.trim_end_matches(|c: char| {
        matches!(c, '/' | '|' | '-' | '—' | '–' | '·' | '(' | '（' | '[' | '【' | '『' | ' ' | '\u{3000}')
    }));
    if let Some(v) = labelled(title_part, TITLE_LABELS) {
        title_part = v;
    }
    let title = trim(strip_brackets(title_part));
    if title.is_empty()
        || title.chars().count() > 40
        || title.contains(['。', '！', '？', '，', '；'])
        || looks_like_junk_title(title)
    {
        return None;
    }

    // Stop the author value before any trailing metadata field ("/ 状态:…", "| …", "【…").
    let raw_author = split.1.split(['/', '|', '【', '『', '（', '(']).next().unwrap_or(split.1);
    let author = trim(raw_author.trim_end_matches(['』', '】', '」', '》', ']', '）', ')']));
    let author = (!author.is_empty()
        && author.chars().count() <= 30
        && !author.contains(['。', '！', '？', '，', '；', ',', ';'])
        && !looks_like_junk_title(author))
    .then(|| author.to_string());

    Some((title.to_string(), author))
}

/// Title and author: labelled lines near the top ("书名：…", Gutenberg's "Title: …"), combined
/// web-novel headers ("《书名》作者：…", "『书名/作者:…』"), or a clean short first line followed by
/// a short name. Returns the index of the first line after them.
fn front_matter(lines: &[&str], headings: &Headings) -> (Option<String>, Option<String>, usize) {
    let mut title = None;
    let mut author = None;
    for line in lines.iter().take(100).map(|l| trim(l)) {
        if line.is_empty() {
            continue;
        }
        if let Some((t, a)) = parse_title_author(line) {
            if title.is_none() {
                title = Some(t);
            }
            if author.is_none() && a.is_some() {
                author = a;
            }
            continue;
        }
        if title.is_none() {
            title = labelled(line, TITLE_LABELS).map(|v| strip_brackets(v).to_string()).filter(|v| !v.is_empty());
        }
        if author.is_none() {
            author = labelled(line, AUTHOR_LABELS).map(|v| strip_brackets(v).to_string()).filter(|v| !v.is_empty());
        }
    }
    let mut start = 0;
    let mut seen = 0;
    for (i, raw) in lines.iter().enumerate() {
        let line = trim(raw);
        if line.is_empty() {
            continue;
        }
        seen += 1;
        let consumed = if labelled(line, TITLE_LABELS).is_some()
            || labelled(line, AUTHOR_LABELS).is_some()
            || parse_title_author(line).is_some()
        {
            true
        } else if seen == 1
            && title.is_none()
            && !headings.is(line)
            && !looks_like_junk_title(line)
            && line.chars().count() <= 30
            && !line.ends_with(['。', '！', '？', '，', '；', '.', '!', '?', ',', ';'])
        {
            title = Some(line.trim_matches(['《', '》']).to_string());
            true
        } else if seen == 2 && start > 0 && author.is_none() && !headings.is(line) {
            let name = line.strip_prefix("by ").or_else(|| line.strip_prefix("By ")).unwrap_or(line);
            let is_name = looks_like_name(name) || (name.len() < line.len() && name.len() <= 40);
            if is_name {
                author = Some(name.to_string());
            }
            is_name
        } else {
            false
        };
        if !consumed {
            break;
        }
        start = i + 1;
        if seen >= 6 {
            break;
        }
    }
    (title, author, start)
}

fn looks_like_name(s: &str) -> bool {
    let n = s.chars().count();
    (2..=6).contains(&n) && s.chars().all(|c| is_cjk(c) || c == '·')
}

fn collapse_inner(s: &str) -> String {
    let mut out = String::new();
    let mut space = false;
    for c in s.chars() {
        if c.is_whitespace() {
            space = !out.is_empty();
        } else {
            if space {
                out.push(' ');
                space = false;
            }
            out.push(c);
        }
    }
    out
}

/// Web-novel TXTs often start with their own table of contents: many headings in a row with
/// nothing between them. Those aren't chapters.
fn drop_contents_lists(chapters: &mut Vec<Chapter>) {
    let mut i = 0;
    while i < chapters.len() {
        let mut j = i;
        while j < chapters.len() && chapters[j].paragraphs.is_empty() && chapters[j].title.is_some() {
            j += 1;
        }
        if j - i >= 3 {
            chapters.drain(i..j);
        } else {
            i = j.max(i + 1);
        }
    }
}

fn split_long(ch: Chapter) -> Vec<Chapter> {
    let total: usize = ch.paragraphs.iter().map(|p| p.chars().count()).sum();
    if total <= MAX_SECTION_CHARS {
        return vec![ch];
    }
    let mut parts = vec![Chapter { title: ch.title.clone(), ..Default::default() }];
    let mut size = 0;
    for p in ch.paragraphs {
        let n = p.chars().count();
        if size + n > PART_CHARS && size > 0 {
            parts.push(Chapter { title: ch.title.clone(), continued: true, ..Default::default() });
            size = 0;
        }
        size += n;
        parts.last_mut().unwrap().paragraphs.push(p);
    }
    parts
}

/// The body for one chapter: `<h2 class="yy-title">` and one `<p>` per paragraph.
pub fn chapter_body(ch: &Chapter) -> Element {
    let mut body = Element::new("body");
    if let Some(t) = ch.title.as_ref().filter(|_| !ch.continued) {
        body.children.push(Node::Element(Element::new("h2").with_attr("class", "yy-title").with_text(t.clone())));
    }
    for p in &ch.paragraphs {
        let mut el = Element::new("p");
        if p == SCENE_BREAK {
            el.set_attr("class", "yy-break");
        }
        body.children.push(Node::Element(el.with_text(p.clone())));
    }
    body
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn decodes_gbk_and_utf8() {
        let gbk = std::fs::read(concat!(env!("CARGO_MANIFEST_DIR"), "/../../fixtures/books/beiying-gbk.txt")).unwrap();
        let utf8 = std::fs::read_to_string(concat!(env!("CARGO_MANIFEST_DIR"), "/../../fixtures/books/beiying.txt")).unwrap();
        let text = decode(&gbk);
        assert!(text.starts_with("背影"));
        assert!(text.contains("我与父亲不相见已二年馀了"));
        let a = structure(&text);
        let b = structure(&utf8);
        assert_eq!(a.title.as_deref(), Some("背影"));
        assert_eq!(b.title.as_deref(), Some("背影"));
        assert_eq!(b.author.as_deref(), Some("朱自清"));
        assert_eq!(a.chapters, b.chapters);
        assert_eq!(b.chapters.len(), 1);
        assert_eq!(b.chapters[0].title.as_deref(), Some("背影"));
        assert_eq!(b.chapters[0].paragraphs.len(), 6);
        assert_eq!(a.language, "zh");
    }

    #[test]
    fn decodes_utf16_with_bom() {
        let mut bytes = vec![0xFF, 0xFE];
        for u in "第一章 开始\n正文。".encode_utf16() {
            bytes.extend_from_slice(&u.to_le_bytes());
        }
        assert_eq!(decode(&bytes), "第一章 开始\n正文。");
    }

    #[test]
    fn headings() {
        assert!(is_heading("第一回\u{3000}甄士隱夢幻識通靈\u{3000}賈雨村風塵怀閨秀"));
        assert!(is_heading("第十四回　　林如海捐館揚州城　賈寶玉路謁北靜王"));
        assert!(is_heading("第 3 章 风起"));
        assert!(is_heading("楔子"));
        assert!(is_heading("CHAPTER XII."));
        assert!(is_heading("Chapter 1"));
        assert!(is_heading("Chapter 3: The Party"));
        assert!(is_heading("BOOK TWENTY-ONE"));
        assert!(is_heading("Chapter XII.—The Return"));
        assert!(is_heading("Prologue"));
        assert!(!is_heading("第三章说的是一件旧事。"));
        assert!(!is_heading("Chapters are long, he said."));
        assert!(!is_heading("Chapter one of my life began in a small town."));
        assert!(!is_heading("Part of the problem was the weather."));
        assert!(!is_heading("Book of the Dead"));
    }

    #[test]
    fn hard_wrapped_chinese_with_chapters() {
        let line = "　　".to_string() + &"字".repeat(30);
        let cont = "字".repeat(33);
        let mut text = String::from("第一回　甄士隱夢幻識通靈\n————————————\n");
        for _ in 0..12 {
            text += &format!("{line}\n{cont}\n{cont}\n短的一行．\n");
        }
        text += "第二回　賈夫人仙逝揚州城\n";
        for _ in 0..12 {
            text += &format!("{line}\n{cont}\n短的．\n");
        }
        let book = structure(&text);
        assert_eq!(book.chapters.len(), 2);
        assert_eq!(book.chapters[0].title.as_deref(), Some("第一回 甄士隱夢幻識通靈"));
        assert_eq!(book.chapters[0].paragraphs.len(), 12);
        assert_eq!(book.chapters[0].paragraphs[0].chars().count(), 30 + 33 + 33 + 5);
        assert_eq!(book.chapters[1].paragraphs.len(), 12);
    }

    #[test]
    fn one_paragraph_per_line() {
        let mut text = String::from("第一章 风起\n");
        for i in 0..40 {
            text += &format!("　　{}。\n", "字".repeat(20 + (i * 7) % 60));
        }
        let book = structure(&text);
        assert_eq!(book.chapters.len(), 1);
        assert_eq!(book.chapters[0].paragraphs.len(), 40);
    }

    #[test]
    fn english_blank_line_paragraphs() {
        let mut text = String::new();
        for i in 0..10 {
            text += &format!("CHAPTER {}\n\n", i + 1);
            for _ in 0..3 {
                text += "It is a truth universally acknowledged, that a single man in\npossession of a good fortune, must be in want of a wife.\n\n";
            }
        }
        let book = structure(&text);
        assert_eq!(book.language, "en");
        assert_eq!(book.chapters.len(), 10);
        assert_eq!(book.chapters[0].paragraphs.len(), 3);
        assert!(book.chapters[0].paragraphs[0].contains("single man in possession"));
    }

    #[test]
    fn gutenberg_header_and_chapter_names() {
        let text = "The Project Gutenberg eBook of Alice's Adventures in Wonderland\n\n\
            Title: Alice's Adventures in Wonderland\n\nAuthor: Lewis Carroll\n\n\
            *** START OF THE PROJECT GUTENBERG EBOOK ALICE'S ADVENTURES IN WONDERLAND ***\n\n\
            CHAPTER I.\n\nDown the Rabbit-Hole\n\n\
            Alice was beginning to get very tired of sitting by her sister on the\nbank.\n\n\
            CHAPTER II.\nThe Pool of Tears\n\n“Curiouser and curiouser!” cried Alice.\n\n\
            *** END OF THE PROJECT GUTENBERG EBOOK ALICE'S ADVENTURES IN WONDERLAND ***\n\nLicence text.\n";
        let book = structure(text);
        assert_eq!(book.title.as_deref(), Some("Alice's Adventures in Wonderland"));
        assert_eq!(book.author.as_deref(), Some("Lewis Carroll"));
        let titles: Vec<_> = book.chapters.iter().map(|c| c.title.as_deref()).collect();
        assert_eq!(
            titles,
            vec![None, Some("CHAPTER I. Down the Rabbit-Hole"), Some("CHAPTER II. The Pool of Tears"), None]
        );
        assert!(book.chapters[0].paragraphs.last().unwrap().starts_with("*** START"));
        assert!(book.chapters[3].paragraphs[0].starts_with("*** END"));
    }

    #[test]
    fn labelled_front_matter() {
        let text = "书名：《三体》\n作者：刘慈欣\n\n第一章 科学边界\n正文。\n";
        let book = structure(text);
        assert_eq!(book.title.as_deref(), Some("三体"));
        assert_eq!(book.author.as_deref(), Some("刘慈欣"));
        assert_eq!(book.chapters.len(), 1);
        assert_eq!(book.chapters[0].title.as_deref(), Some("第一章 科学边界"));
        // A bare heading's name on the next line joins it, but the first sentence doesn't.
        let book = structure("红楼梦\n第一回\n\n甄士隐梦幻识通灵\n\n第二回\n\n他来了。\n");
        assert_eq!(book.author, None);
        let titles: Vec<_> = book.chapters.iter().map(|c| c.title.clone().unwrap()).collect();
        assert_eq!(titles, vec!["第一回 甄士隐梦幻识通灵", "第二回"]);
    }

    #[test]
    fn classical_novel_headings() {
        let wrapped = |s: &str| format!("{s}\n");
        let mut text = String::new();
        // A bare number, separator lines, then the couplet on its own line.
        text += "第三回\n\n\n————————————————\n\n———-\n\n　　　　　　　　 賈雨村夤緣复舊職　林黛玉拋父進京都\n\n\n";
        text += &wrapped("　　卻說雨村忙回頭看時，不是別人，乃是當日同僚一案參革的號張如圭者．");
        // The couplet followed directly by text that isn't indented.
        text += "第八回\n————\n比通靈金鶯微露意　探寶釵黛玉半含酸\n話說鳳姐和寶玉回家．\n";
        // The first words of the chapter ran on into the heading line.
        text += "第三十一回　 撕扇子作千金一笑　因麒麟伏白首雙星　話\n說端陽佳節．\n";
        // The same heading twice around a separator.
        text += "第四十五回　 金蘭契互剖金蘭語　風雨夕悶制風雨詞\n————————\n\n\n第四十五回　 金蘭契互剖金蘭語　風雨夕悶制風雨詞\n\n　　話說鳳姐兒正撫恤平兒．\n";
        let chapters = chapters(&text);
        let titles: Vec<_> = chapters.iter().map(|c| c.title.clone().unwrap()).collect();
        assert_eq!(
            titles,
            vec![
                "第三回 賈雨村夤緣复舊職 林黛玉拋父進京都",
                "第八回 比通靈金鶯微露意 探寶釵黛玉半含酸",
                "第三十一回 撕扇子作千金一笑 因麒麟伏白首雙星",
                "第四十五回 金蘭契互剖金蘭語 風雨夕悶制風雨詞",
            ]
        );
        assert_eq!(chapters[1].paragraphs, vec!["話說鳳姐和寶玉回家．"]);
        assert_eq!(chapters[2].paragraphs, vec!["話說端陽佳節．"]);
        assert_eq!(chapters[3].paragraphs.len(), 1);
    }

    #[test]
    fn contents_list_is_dropped() {
        let text = "我的书\n第一章 开始\n第二章 经过\n第三章 结束\n\n第一章 开始\n正文一。\n第二章 经过\n正文二。\n第三章 结束\n正文三。\n";
        let book = structure(text);
        assert_eq!(book.title.as_deref(), Some("我的书"));
        let titles: Vec<_> = book.chapters.iter().map(|c| c.title.clone().unwrap()).collect();
        assert_eq!(titles, vec!["第一章 开始", "第二章 经过", "第三章 结束"]);
    }

    #[test]
    fn long_chapters_are_split() {
        let para = "长".repeat(1000);
        let text = format!("第一章 很长\n{}", (0..100).map(|_| format!("{para}\n")).collect::<String>());
        let book = structure(&text);
        assert!(book.chapters.len() >= 3);
        assert_eq!(book.chapters[0].title.as_deref(), Some("第一章 很长"));
        assert!(!book.chapters[0].continued);
        assert!(book.chapters[1].continued);
        assert_eq!(book.chapters[1].title.as_deref(), Some("第一章 很长"));
        assert!(!chapter_body(&book.chapters[1]).find(&|e| e.is("h2")).is_some());
    }

    #[test]
    fn web_novel_front_matter_skips_ads_and_parses_inline_title_author() {
        // Case 1: download-site promo line before "《书名》作者：…"
        let text = concat!(
            "推荐一个小说下载必备网址：www.688txt.com\n",
            "每天更新，喜欢的去看看。\n",
            "《暴风城打工实录》作者：又一个鱼雷\n",
            "作品相关\n\n",
            "第一卷\n",
            "第001章 联盟远征军\n",
            "　　正文内容。\n",
        );
        let book = structure(text);
        assert_eq!(book.title.as_deref(), Some("暴风城打工实录"));
        assert_eq!(book.author.as_deref(), Some("又一个鱼雷"));

        // Case 2: bracketed "『书名/作者:…』"
        let text2 = concat!(
            "『神农道君/作者:神威校尉』\n",
            "『状态:更新到:第687章 山猫流浪记（八）』\n",
            "『内容简介:\n",
            "【国风游戏】【全新设定】\n\n",
            "第1章 山村少年\n",
            "　　正文。\n",
        );
        let book2 = structure(text2);
        assert_eq!(book2.title.as_deref(), Some("神农道君"));
        assert_eq!(book2.author.as_deref(), Some("神威校尉"));

        // Case 3: promo-only header with no title inside -> returns None so filename fallback wins
        let text3 = concat!(
            "更多精彩小说请访问 www.example.com\n",
            "本书由网友整理，仅供试读。\n\n",
            "第1章 开始\n",
            "　　正文。\n",
        );
        let book3 = structure(text3);
        assert_eq!(book3.title, None);
    }

    #[test]
    fn web_novel_headings_with_punctuation_prefixes_and_compact_titles() {
        assert!(is_heading("第251章：谁说我不行？！（求月票！）"));
        assert!(is_heading("第100章 什么？竟然是他！"));
        assert!(is_heading("第1章山村少年"));
        assert!(is_heading("第250章（上）"));
        assert!(is_heading("正文 第1章 联盟远征军"));
        assert!(is_heading("正文卷 第001章 联盟远征军"));
        assert!(is_heading("第一卷 第2章 出发"));
        assert!(is_heading("第一卷 农家子弟 第2章 出发"));
        assert!(is_heading("【第3章 大战】"));
        assert!(is_heading("☆、第4章 尾声"));
        assert!(is_heading("=== 第5章 结局 ==="));
        assert!(is_heading("番外1 归途"));
        assert!(is_heading("大结局"));
        // Prose sentences must still be rejected.
        assert!(!is_heading("第三章说的是一件旧事。"));
        assert!(!is_heading("第三章里写着他的名字"));
        assert!(!is_heading("“第一章？”他疑惑地问道。"));
    }

    #[test]
    fn bare_numeric_chapters_detected_without_breaking_prose_lists() {
        let para = "　　这是一段很长的小说正文，用来模拟真实的章节内容，确保每章字数足够多。".repeat(25);
        let mut novel = String::from("『神农道君/作者:神威校尉』\n\n");
        for i in 1..=10 {
            novel.push_str(&format!("{i:03} 山猫流浪记（{i}）\n{para}\n\n"));
        }
        let book = structure(&novel);
        assert_eq!(book.title.as_deref(), Some("神农道君"));
        assert_eq!(book.chapters.len(), 10, "expected 10 numeric chapters, got {:?}", book.chapters.iter().map(|c| &c.title).collect::<Vec<_>>());

        // Short numbered list in a chapter should NOT be split into chapters.
        let mut essay = String::from("第1章 随笔\n");
        for i in 1..=6 {
            essay.push_str(&format!("{i}. 第{i}点想法\n"));
        }
        essay.push_str(&para);
        let book2 = structure(&essay);
        assert_eq!(book2.chapters.len(), 1);
    }

    #[test]
    fn long_chapter_ignores_volume_headers_and_normal_ten_thousand_char_chapters() {
        let mut outline = vec![(Some("第一卷".into()), 0, 0)];
        for i in 1..=20 {
            outline.push((Some(format!("第{i}章")), 4_000, i));
        }
        // A single 10,487-char chapter in a 4,000-char median novel is normal, not missing headings.
        outline.push((Some("第21章".into()), 10_487, 21));
        assert_eq!(long_chapter(&outline), None);
        // A 25,000-char stretch (> 4x median) is flagged.
        outline.push((Some("第22章".into()), 25_000, 22));
        assert_eq!(long_chapter(&outline), Some(22));
    }
}
