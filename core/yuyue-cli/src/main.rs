//! The core from the command line, for development.
//!
//! ```text
//! yuyue import <library> <file>...
//! yuyue list <library>
//! yuyue toc <library> <book>
//! yuyue doc <library> <book> <section> [--native]
//! yuyue sentences <library> <book> <section>
//! yuyue site <library> <out> [--app <reader build>] [--native]
//! ```
//!
//! `<book>` is an id or the start of one. `site` writes every book as static files laid out
//! like the app's URLs (`/book/<id>/s/<n>.html`, `/book/<id>/r/…`, `/app/…`), so the reader
//! can be tried in a desktop browser: `python3 -m http.server -d <out>`.

use std::fmt::Write as _;
use std::fs;
use std::path::Path;
use std::process::ExitCode;
use std::sync::Arc;

use yuyue_core::document::Paging;
use yuyue_core::{BookSummary, CoreError, MarkStyle, PageGeometry, Position, SectionKind, TextPoint, YuyueCore};

const USAGE: &str = "usage:
  yuyue import <library> <file>...
  yuyue list <library>
  yuyue toc <library> <book>
  yuyue doc <library> <book> <section> [--native]
  yuyue sentences <library> <book> <section>
  yuyue site <library> <out> [--app <reader build>] [--native]
  yuyue progress <library> <book> <section> <sentence>
  yuyue heard <library> <book> <section> <sentence>
  yuyue word <library> <book> <section> <sentence> <word> <phonetic|-> <meaning> <context>
  yuyue words <library>
  yuyue export <library>          (every change, as a device puts it in the sync folder)
  yuyue import-changes <library> <file>
  yuyue devices <library>
  yuyue lines <library> <book> <section>    (剧本: each quote and who says it, ? when unsure)
  yuyue pinyin <text file>          (读音: each line's pinyin, toneless)
  yuyue fa <tsv> <zh|en> <emissions.f32> <seconds> [<emissions.f32> <seconds>]…
                                   (强制对齐 sentences first–last of a section against the
                                    model's output, raw little-endian f32, 28 a frame)
  yuyue digest <file>              (BLAKE3, as the apps check a downloaded model)
  yuyue marks <m4b>                (an M4B's chapter marks: start in seconds, title)
  yuyue drama <library> <book> <section>       (广播剧: each stretch, who, how sure, acting)
  yuyue drama-cast <library> <book>            (广播剧: the cast, most lines first)
  yuyue drama-sounds <dir>                     (广播剧: the built-in room tones and sounds, as WAV to listen to)
  yuyue drama-eval <library> <book> <answers.jsonl>…   (广播剧: score against answer sheets)
  yuyue audiobook-eval <library> <book> <first> <last> (有声书: a made-up reading, aligned and scored)
  yuyue drama-llm <library> <book> <first> <last> <base url> <model> [<verify model>]
                                   (广播剧: 认人 for sections first–last through an OpenAI-style chat
                                    endpoint — the whole book's cast, then each scene; answers are
                                    kept by their question in <library>/llm-cache, asked once)
  yuyue audiobook-letters <library> <book> <emissions.f32> <seconds>
                                   (有声书 without a transcriber: the alignment model's letters as the
                                    transcript — which book, then each sentence's rough time)";

fn main() -> ExitCode {
    let args: Vec<String> = std::env::args().skip(1).collect();
    match run(&args) {
        Ok(()) => ExitCode::SUCCESS,
        Err(e) => {
            eprintln!("yuyue: {e}");
            ExitCode::FAILURE
        }
    }
}

fn run(args: &[String]) -> Result<(), String> {
    let native = args.iter().any(|a| a == "--native");
    let flag_value = |flag: &str| -> Result<Option<String>, String> {
        args.iter()
            .position(|a| a == flag)
            .map(|i| args.get(i + 1).cloned().ok_or_else(|| format!("{flag} needs a value")))
            .transpose()
    };
    let app = flag_value("--app")?;
    let device_name = flag_value("--device")?.unwrap_or_else(|| "yuyue cli".into());
    let platform_name = flag_value("--platform")?.unwrap_or_else(|| "cli".into());
    let value_flags = ["--app", "--device", "--platform"];
    let plain: Vec<&str> = args
        .iter()
        .enumerate()
        .filter(|(i, a)| {
            *a != "--native"
                && !value_flags.contains(&a.as_str())
                && !(*i > 0 && value_flags.contains(&args[i - 1].as_str()))
        })
        .map(|(_, a)| a.as_str())
        .collect();
    let geometry = PageGeometry {
        paging: if native { Paging::Native } else { Paging::Snap },
        margin: 24.0,
        top: 56.0,
        bottom: 88.0,
        columns: 1,
        reduced_motion: false,
    };
    let open = |library: &str| YuyueCore::open(library.to_string(), device_name.clone(), platform_name.clone()).map_err(err);
    match plain.as_slice() {
        ["import", library, files @ ..] if !files.is_empty() => {
            let core = open(library)?;
            for file in files {
                match core.import_book(file.to_string()) {
                    Ok(b) => println!("{}  {}  ({} sections)", b.id, b.title, core.book(b.id.clone()).map_err(err)?.sections.len()),
                    Err(e) => eprintln!("{file}: {e}"),
                }
            }
            Ok(())
        }
        ["list", library] => {
            for b in open(library)?.books().map_err(err)? {
                println!(
                    "{}  {} — {}  [{}]  读 {} 分钟 · 听 {} 分钟  {:.0}%",
                    b.id,
                    b.title,
                    b.authors.join(", "),
                    b.language,
                    b.read_minutes,
                    b.listen_minutes,
                    b.progress * 100.0
                );
            }
            Ok(())
        }
        ["toc", library, book] => {
            let core = open(library)?;
            let detail = core.book(find(&core, book)?.id).map_err(err)?;
            println!("start: section {}", detail.start);
            for e in &detail.toc {
                let anchor = e.anchor.as_deref().map(|a| format!("#{a}")).unwrap_or_default();
                println!("{}{}  → {}{anchor}", "  ".repeat(e.depth as usize), e.title, e.section);
            }
            println!();
            for (i, s) in detail.sections.iter().enumerate() {
                let kind = match s.kind {
                    SectionKind::Text => "",
                    SectionKind::Image => " image",
                    SectionKind::Boilerplate => " boilerplate",
                };
                let flags = format!("{kind}{}{}", if s.linear { "" } else { " non-linear" }, if s.continued { " continued" } else { "" });
                println!("#{i} {}  {} sentences, {} units{flags}", s.title.as_deref().unwrap_or("—"), s.sentences, s.units);
            }
            Ok(())
        }
        ["doc", library, book, section] => {
            let core = open(library)?;
            let id = find(&core, book)?.id;
            print!("{}", core.section_document(id, number(section)?, geometry).map_err(err)?);
            Ok(())
        }
        ["dict-import", library, file] => {
            let d = open(library)?.import_dictionary(file.to_string()).map_err(err)?;
            println!("{}  {}  {} entries", d.id, d.title, d.entries);
            Ok(())
        }
        ["lookup", library, word] => {
            match open(library)?.look_up(word.to_string()).map_err(err)? {
                Some(e) => println!("{} · {}\n{}", e.dictionary, e.headword, e.summary),
                None => println!("(not found)"),
            }
            Ok(())
        }
        ["script", library, book, to] => {
            let core = open(library)?;
            let id = find(&core, book)?.id;
            let script = match *to {
                "simplified" => Some(yuyue_core::Script::Simplified),
                "traditional" => Some(yuyue_core::Script::Traditional),
                _ => None,
            };
            core.set_book_script(id, script).map_err(err)?;
            Ok(())
        }
        ["sentences", library, book, section] => {
            let core = open(library)?;
            let id = find(&core, book)?.id;
            for (i, s) in core.sentences(id, number(section)?).map_err(err)?.iter().enumerate() {
                println!("{i}\t{s}");
            }
            Ok(())
        }
        ["progress", library, book, section, sentence] => {
            let core = open(library)?;
            let id = find(&core, book)?.id;
            let position = Position { section: number(section)?, sentence: number(sentence)? };
            core.save_progress(id, position).map_err(err)
        }
        // 双击耳机: highlights a whole sentence as if marked while listening.
        ["heard", library, book, section, sentence] => {
            let core = open(library)?;
            let id = find(&core, book)?.id;
            let (section, sentence): (u32, u32) = (number(section)?, number(sentence)?);
            let raw = core.sentences(id.clone(), section).map_err(err)?.into_iter().nth(sentence as usize).ok_or("no such sentence")?;
            let text = raw.trim();
            let lead = raw.len() - raw.trim_start().len();
            let at = |bytes: usize| TextPoint { sentence, offset: raw[..bytes].encode_utf16().count() as u32 };
            let mark = core.mark_heard(id, section, at(lead), at(lead + text.len()), text.to_string(), MarkStyle::Yellow).map_err(err)?;
            println!("{}", mark.text);
            Ok(())
        }
        ["word", library, book, section, sentence, word, phonetic, meaning, context] => {
            let core = open(library)?;
            let id = find(&core, book)?.id;
            let w = core
                .add_word(word.to_string(), Some(phonetic.to_string()).filter(|p| p != "-"), Some(meaning.to_string()), id, number(section)?, number(sentence)?, context.to_string())
                .map_err(err)?;
            println!("{} · {}", w.word, w.meaning.unwrap_or_default());
            Ok(())
        }
        ["words", library] => {
            for w in open(library)?.words().map_err(err)? {
                println!("{}\t{}\t{}", w.word, if w.known { "掌握了" } else { "在学" }, w.meaning.unwrap_or_default());
            }
            Ok(())
        }
        ["export", library] => {
            print!("{}", open(library)?.export_changes("{}".into()).map_err(err)?);
            Ok(())
        }
        ["import-changes", library, file] => {
            let batch = fs::read_to_string(file).map_err(|e| e.to_string())?;
            println!("{} values changed", open(library)?.import_changes(batch).map_err(err)?);
            Ok(())
        }
        ["devices", library] => {
            for d in open(library)?.devices().map_err(err)? {
                let this = if d.this_device { " (this)" } else { "" };
                println!("{}  {} [{}]{this}  last: {:?} {:?} at {:?}", d.id, d.name, d.platform, d.last_book, d.last_fraction, d.last_at);
            }
            Ok(())
        }
        ["device-id", library] => {
            println!("{}", open(library)?.device_id());
            Ok(())
        }
        ["original-file", library, book] => {
            let core = open(library)?;
            let id = find(&core, book)?.id;
            println!("{}", core.original_file(id).map_err(err)?);
            Ok(())
        }
        ["delete-book", library, book] => {
            let core = open(library)?;
            let id = find(&core, book)?.id;
            core.delete_book(id).map_err(err)
        }
        ["mark", library, book, section, sentence, text, note] => {
            let core = open(library)?;
            let id = find(&core, book)?.id;
            let sen = number(sentence)?;
            let len = text.encode_utf16().count() as u32;
            let note_opt = if *note == "-" { None } else { Some(note.to_string()) };
            let m = core
                .add_mark(
                    id,
                    number(section)?,
                    TextPoint { sentence: sen, offset: 0 },
                    TextPoint { sentence: sen, offset: len },
                    text.to_string(),
                    MarkStyle::Yellow,
                    note_opt,
                )
                .map_err(err)?;
            println!("{}", m.id);
            Ok(())
        }
        ["mark-note", library, book, mark_id, note] => {
            let core = open(library)?;
            let id = find(&core, book)?.id;
            let note_opt = if *note == "-" { None } else { Some(note.to_string()) };
            core.set_mark_note(id, mark_id.to_string(), note_opt).map_err(err)
        }
        ["marks", library, book] => {
            let core = open(library)?;
            let id = find(&core, book)?.id;
            let items: Vec<_> = core
                .marks(id)
                .map_err(err)?
                .into_iter()
                .map(|m| serde_json::json!({ "id": m.id, "section": m.section, "text": m.text, "note": m.note, "style": format!("{:?}", m.style) }))
                .collect();
            println!("{}", serde_json::to_string(&items).map_err(|e| e.to_string())?);
            Ok(())
        }
        ["conflicts", library] => {
            let items: Vec<_> = open(library)?
                .note_conflicts()
                .map_err(err)?
                .into_iter()
                .map(|c| {
                    serde_json::json!({
                        "book": c.book,
                        "mark": c.mark,
                        "older_note": c.older.note,
                        "newer_note": c.newer.note,
                        "older_device": c.older.device_name,
                        "newer_device": c.newer.device_name,
                    })
                })
                .collect();
            println!("{}", serde_json::to_string(&items).map_err(|e| e.to_string())?);
            Ok(())
        }
        ["resolve-conflict", library, book, mark_id, choice] => {
            use yuyue_core::conflicts::ConflictChoice;
            let core = open(library)?;
            let id = find(&core, book)?.id;
            let c = match *choice {
                "older" => ConflictChoice::Older,
                "newer" => ConflictChoice::Newer,
                "both" => ConflictChoice::Both,
                _ => return Err("choice: older | newer | both".into()),
            };
            core.resolve_note_conflict(id, mark_id.to_string(), c).map_err(err)
        }
        ["settings", library] => {
            let s = open(library)?.reader_settings().map_err(err)?;
            println!(
                "{}",
                serde_json::json!({ "paper": s.paper, "mode": s.mode, "font": s.font, "font_size": s.font_size, "compress": s.compress })
            );
            Ok(())
        }
        ["settings", library, paper, font_size] => {
            use yuyue_core::document::Paper;
            let core = open(library)?;
            let mut s = core.reader_settings().map_err(err)?;
            if *paper != "-" {
                s.paper = match *paper {
                    "plain" => Paper::Plain,
                    "dark" => Paper::Dark,
                    "kami" => Paper::Kami,
                    "kaminight" => Paper::KamiNight,
                    "sage" => Paper::Sage,
                    "eink" => Paper::Eink,
                    _ => return Err(format!("unknown paper: {paper}")),
                };
            }
            if *font_size != "-" {
                s.font_size = font_size.parse().map_err(|_| format!("bad font_size: {font_size}"))?;
            }
            core.set_reader_settings(s).map_err(err)
        }
        ["make-snapshot", library, out_file] => {
            use yuyue_core::snapshot::SnapshotParts;
            let bytes = open(library)?
                .make_snapshot(
                    out_file.to_string(),
                    SnapshotParts { progress: true, notes: true, words: true, settings: true, files: true },
                )
                .map_err(err)?;
            println!("{bytes}");
            Ok(())
        }
        ["drama", library, book, section] => {
            let core = open(library)?;
            let id = find(&core, book)?.id;
            let n = number(section)?;
            let cast = core.drama_cast(id.clone(), u32::MAX).map_err(err)?;
            let name = |i: u32| cast.iter().find(|c| c.id == i).map(|c| c.names[0].clone()).unwrap_or_else(|| format!("#{i}"));
            let play = core.drama_section(id, n).map_err(err)?;
            for sc in &play.scenes {
                println!("— 场 {}–{} {} [{}] {}", sc.first, sc.last, sc.place, sc.place_kind, sc.present.iter().map(|&i| name(i)).collect::<Vec<_>>().join("、"));
            }
            for s in &play.segments {
                let who = match &s.speaker {
                    yuyue_core::drama::DramaSpeaker::Char { id } => name(*id),
                    yuyue_core::drama::DramaSpeaker::Group { name } => format!("[{name}]"),
                    yuyue_core::drama::DramaSpeaker::Narrator => "旁白".into(),
                    yuyue_core::drama::DramaSpeaker::Unsure { top } => format!("? {}", top.iter().map(|c| format!("{}{:.0}%", name(c.id), c.p * 100.0)).collect::<Vec<_>>().join(" ")),
                };
                let act = if s.act.level > 0 || s.act.volume != yuyue_core::drama::ActVolume::Normal { format!(" ({:?}{} {:?})", s.act.emotion, s.act.level, s.act.volume) } else { String::new() };
                let tag = if s.tag_only { " ·tag" } else { "" };
                println!("{}\t{:?}\t{who}\t{:?} {:.2}{act}{tag}\t{}", s.sentence, s.kind, s.decided_by, s.p_correct, s.text);
            }
            Ok(())
        }
        // 发版: a version's notes from CHANGELOG.md — `md` for the GitHub release, `html-zh` /
        // `html-en` for the Mac's appcast.
        ["release-notes", log, version, format] => {
            use yuyue_core::changelog::{parse_changelog, release_html, release_markdown};
            let text = fs::read_to_string(log).map_err(|e| e.to_string())?;
            let r = parse_changelog(text).into_iter().find(|r| r.version == *version).ok_or_else(|| format!("CHANGELOG has no {version}"))?;
            let out = match *format {
                "md" => release_markdown(&r),
                "html-zh" => release_html(&r, true),
                "html-en" => release_html(&r, false),
                _ => return Err("format: md | html-zh | html-en".into()),
            };
            if out.trim().is_empty() || out == "<ul></ul>" {
                return Err(format!("{version} has no notes"));
            }
            print!("{out}");
            Ok(())
        }
        // update.json for the apps: the version, Android's code and APK, and the notes.
        ["update-json", log, version, code, page, apk, size, sha256, changelog] => {
            use yuyue_core::changelog::{Notes, UpdateInfo, parse_changelog};
            let text = fs::read_to_string(log).map_err(|e| e.to_string())?;
            let r = parse_changelog(text).into_iter().find(|r| r.version == *version).ok_or_else(|| format!("CHANGELOG has no {version}"))?;
            let u = UpdateInfo {
                version: r.version,
                code: code.parse().map_err(|_| "code: a number")?,
                date: r.date,
                page: page.to_string(),
                apk: apk.to_string(),
                size: size.parse().map_err(|_| "size: a number")?,
                sha256: sha256.to_string(),
                changelog: changelog.to_string(),
                notes: Notes { zh: r.notes_zh, en: r.notes_en },
            };
            println!("{}", serde_json::to_string_pretty(&u).map_err(|e| e.to_string())?);
            Ok(())
        }
        ["drama-sounds", dir] => {
            fs::create_dir_all(dir).map_err(|e| e.to_string())?;
            for a in yuyue_core::drama::builtin::assets() {
                let (samples, stereo) = yuyue_core::drama::builtin::samples(&a.id).ok_or("not made")?;
                // The WAV writer takes stereo; a mono sound goes to both sides.
                let both: Vec<f32> = if stereo { samples.to_vec() } else { samples.iter().flat_map(|x| [*x, *x]).collect() };
                let name = format!("{}/{}.wav", dir, a.id.trim_start_matches(yuyue_core::drama::builtin::PREFIX));
                fs::write(&name, yuyue_core::drama::mixer::wav(&both)).map_err(|e| e.to_string())?;
                println!("{name}\t{}\t{:.1} s", a.key, a.seconds);
            }
            Ok(())
        }
        ["drama-cast", library, book] => {
            let core = open(library)?;
            let id = find(&core, book)?.id;
            let t = std::time::Instant::now();
            let cast = core.drama_cast(id.clone(), u32::MAX).map_err(err)?;
            eprintln!("整本规则 {:?}", t.elapsed());
            for c in cast {
                println!("{}\t{}\t{:?}\t{} 句\t第 {} 节起", c.id, c.names.join("、"), c.gender, c.lines, c.first_section.unwrap_or(0));
            }
            Ok(())
        }
        ["drama-eval", library, book, answers @ ..] if !answers.is_empty() => {
            use yuyue_core::drama::eval;
            let core = open(library)?;
            let id = find(&core, book)?.id;
            let table = yuyue_core::drama::cast::CastTable::new(core.drama_cast(id.clone(), u32::MAX).map_err(err)?);
            let mut total = eval::Score::default();
            for a in answers {
                let (head, gold) = eval::read(&fs::read_to_string(a).map_err(|e| e.to_string())?).ok_or("bad answer sheet")?;
                let play = core.drama_section(id.clone(), head.section).map_err(err)?;
                let sentences = core.sentences(id.clone(), head.section).map_err(err)?;
                let s = eval::score(&play, &table, &sentences, &gold);
                println!("{a}: 台词 {} 认对 {} 认错 {} 没把握 {}", s.lines, s.right, s.wrong.len(), s.unsure);
                for m in &s.wrong {
                    println!("  错 {} {}：应为 {}，认成 {}（{:?}）", m.sentence, m.head, m.want, m.got, m.by);
                }
                total.add(&s);
            }
            println!("合计：台词 {}，配到人 {:.1}%，配了的里面错 {:.2}%，规则认错 {}，不是台词却配了人 {}", total.lines, total.coverage() * 100.0, total.error_rate() * 100.0, total.rule_wrong, total.not_speech_voiced);
            for (k, r, w, u) in &total.by_kind {
                println!("  {k}: 对 {r} 错 {w} 没把握 {u}");
            }
            Ok(())
        }
        ["audiobook-eval", library, book, first, last] => {
            use yuyue_core::audiobook::eval::{Variant, make_in as make, run_in as run};
            let core = open(library)?;
            let id = find(&core, book)?.id;
            let sections: Vec<Vec<String>> = (number(first)?..=number(last)?).map(|i| core.sentences(id.clone(), i).unwrap_or_default()).collect();
            let text: String = sections.iter().flatten().take(200).cloned().collect();
            let zh = text.chars().filter(|c| ('\u{4e00}'..='\u{9fff}').contains(c)).count() * 3 >= text.chars().filter(char::is_ascii_alphabetic).count();
            let variants = [
                ("照念", Variant { homophone: 0.05, wrong: 0.03, ..Default::default() }),
                ("删 6% + 一整段", Variant { drop: 0.06, drop_run: 25, homophone: 0.05, wrong: 0.03, ..Default::default() }),
                ("头中尾插话", Variant { extra: 50.0, homophone: 0.05, wrong: 0.03, ..Default::default() }),
                ("都有", Variant { drop: 0.06, drop_run: 25, extra: 50.0, homophone: 0.08, wrong: 0.05 }),
            ];
            for (name, v) in variants {
                let t = std::time::Instant::now();
                let made = make(&sections, &v, 7, zh);
                let s = run(&sections, &made, 0.05, 7, zh);
                println!(
                    "{name}: 句 {} · 精确 {:.1}% · 偏出 0.3 s {:.2}% · 点错句 {:.2}% · 没念却给了时间 {} · 念了却判没念 {} · 插话漏进 {:.1}/{:.1} s · {:?}",
                    s.sentences, s.auto_share() * 100.0, s.off_rate() * 100.0, s.wrong_rate() * 100.0, s.unread_given_time, s.read_called_unread, s.extra_leaked, s.extra_total, t.elapsed()
                );
            }
            Ok(())
        }
        ["drama-llm", library, book, first, last, base, model, rest @ ..] if rest.len() <= 1 => {
            use yuyue_core::drama::api::{DramaCaps, DramaResult, DramaTask, LlmTier};
            let core = open(library)?;
            let id = find(&core, book)?.id;
            let (first, last) = (number(first)?, number(last)?);
            let verify_model = rest.first().map(|s| s.to_string());
            core.drama_set_caps(DramaCaps { llm: true, verify_distinct: verify_model.is_some(), context_tokens: 1_000_000, combo: format!("{model}+{model}+{}", verify_model.as_deref().unwrap_or(model)), ..Default::default() });
            let cache = Path::new(library).join("llm-cache");
            fs::create_dir_all(&cache).map_err(|e| e.to_string())?;
            let started = std::time::Instant::now();
            let (mut asked, mut cached, mut failed) = (0, 0, 0);
            let mut answered = std::collections::HashSet::new();
            loop {
                let Some(task) = core.drama_next_task(id.clone(), first).map_err(err)? else { break };
                let DramaTask::Llm { id: task_id, tier, system, user, schema, max_output_tokens, .. } = task else {
                    let tid = match &task { DramaTask::Tts { id, .. } | DramaTask::DesignVoice { id, .. } | DramaTask::Sfx { id, .. } | DramaTask::Llm { id, .. } => id.clone() };
                    core.drama_submit(id.clone(), tid, DramaResult::Returned).map_err(err)?;
                    break;
                };
                // Only the sections asked for (and the whole book's cast).
                let parts: Vec<&str> = task_id.split(':').collect();
                if matches!(parts.first(), Some(&("attr" | "verify" | "direct")))
                    && parts.get(2).and_then(|x| x.parse::<u32>().ok()).is_some_and(|s| s < first || s > last) {
                        core.drama_submit(id.clone(), task_id, DramaResult::Returned).map_err(err)?;
                        break;
                    }
                let m = if tier == LlmTier::Verify { verify_model.clone().unwrap_or_else(|| model.to_string()) } else { model.to_string() };
                let key = blake3::hash(format!("{m}\u{1}{system}\u{1}{user}").as_bytes()).to_hex()[..24].to_string();
                let file = cache.join(format!("{key}.txt"));
                // Asked again: the answer kept didn't pass; ask afresh.
                let again = !answered.insert(task_id.clone());
                let text = if let (false, Ok(t)) = (again, fs::read_to_string(&file)) {
                    cached += 1;
                    Some(t)
                } else {
                    asked += 1;
                    // Structured output, as the apps ask for it; the schema in the instructions too,
                    // for a service that takes the format but not the schema.
                    let schema_v: serde_json::Value = serde_json::from_str(&schema).unwrap_or(serde_json::Value::Null);
                    let body = serde_json::json!({
                        "model": m,
                        "messages": [{"role": "system", "content": format!("{system}\n\n只回一个 JSON，严格照这个 JSON Schema（字段名一个不改）：\n{schema}")}, {"role": "user", "content": user}],
                        "max_tokens": max_output_tokens.max(4096),
                        "temperature": 0,
                        "response_format": {"type": "json_schema", "json_schema": {"name": "answer", "strict": true, "schema": schema_v}},
                    });
                    let out = std::process::Command::new("curl")
                        .args(["-s", "-m", "900", "-H", "Content-Type: application/json", "--data-binary", "@-", &format!("{}/chat/completions", base.trim_end_matches('/'))])
                        .stdin(std::process::Stdio::piped())
                        .stdout(std::process::Stdio::piped())
                        .spawn()
                        .and_then(|mut c| {
                            use std::io::Write;
                            c.stdin.take().unwrap().write_all(body.to_string().as_bytes())?;
                            c.wait_with_output()
                        })
                        .map_err(|e| e.to_string())?;
                    let v: serde_json::Value = serde_json::from_slice(&out.stdout).unwrap_or_default();
                    let t = v["choices"][0]["message"]["content"].as_str().map(str::to_string);
                    match &t {
                        Some(t) => fs::write(&file, t).map_err(|e| e.to_string())?,
                        None => eprintln!("  {task_id}: no answer ({})", String::from_utf8_lossy(&out.stdout).chars().take(200).collect::<String>()),
                    }
                    t
                };
                let result = match text {
                    Some(text) => DramaResult::Llm { text },
                    None => {
                        failed += 1;
                        DramaResult::Failed { reason: "no answer".into() }
                    }
                };
                let p = core.drama_submit(id.clone(), task_id.clone(), result).map_err(err)?;
                eprintln!("{task_id} [{m}] · {}/{} sections · cast {} · {:?}", p.sections_done, p.sections, p.cast_ready, started.elapsed());
            }
            println!("asked {asked}, from cache {cached}, failed {failed}, {:?}", started.elapsed());
            Ok(())
        }
        // 声纹: a WAV's model input (frames × 80, f32 little-endian) written out, for trying a
        // speaker model on it outside the app.
        ["speaker-features", wav, out] => {
            let bytes = fs::read(wav).map_err(|e| e.to_string())?;
            let (samples, rate) = read_wav(&bytes).ok_or("not a 16-bit or float WAV")?;
            let f = yuyue_core::drama::speaker::features(&samples, rate);
            let raw: Vec<u8> = f.data.iter().flat_map(|v| v.to_le_bytes()).collect();
            fs::write(out, raw).map_err(|e| e.to_string())?;
            println!("{}", f.frames);
            Ok(())
        }
        // 听写 (SenseVoice) outside the app: the model's input for a WAV (frames × dim, f32), and
        // the words from its best token each frame (i32). `meta` is the model's metadata as JSON.
        ["asr-input", meta, tokens, wav, out] => {
            let m = sense_voice(meta, tokens)?;
            let bytes = fs::read(wav).map_err(|e| e.to_string())?;
            let (samples, rate) = read_wav(&bytes).ok_or("not a 16-bit or float WAV")?;
            let a = m.input(samples, rate);
            let raw: Vec<u8> = a.data.iter().flat_map(|v| v.to_le_bytes()).collect();
            fs::write(out, raw).map_err(|e| e.to_string())?;
            println!("{} {}", a.frames, a.dim);
            Ok(())
        }
        ["asr-words", meta, tokens, ids] => {
            let m = sense_voice(meta, tokens)?;
            let bytes = fs::read(ids).map_err(|e| e.to_string())?;
            let best: Vec<i32> = bytes.chunks_exact(4).map(|b| i32::from_le_bytes([b[0], b[1], b[2], b[3]])).collect();
            for w in m.words(best, 0.0) {
                println!("{:7.2} {:7.2} {}", w.t0, w.t1, w.text);
            }
            Ok(())
        }
        ["audiobook-letters", library, book, emissions, seconds] => {
            use yuyue_core::audiobook::anchor::{BookToks, Class, HeardToks, Units, coarse_in, heard_letters};
            use yuyue_core::audiobook::matchbook::{KIndex, best};
            let core = open(library)?;
            let id = find(&core, book)?.id;
            let detail = core.book(id.clone()).map_err(err)?;
            let zh = detail.summary.language.starts_with("zh");
            let sections: Vec<Vec<String>> = (0..detail.sections.len() as u32).map(|i| core.sentences(id.clone(), i).unwrap_or_default()).collect();
            let bytes = std::fs::read(emissions).map_err(|e| e.to_string())?;
            let em: Vec<f32> = bytes.chunks_exact(4).map(|b| f32::from_le_bytes([b[0], b[1], b[2], b[3]])).collect();
            let frames = em.len() / 28;
            let t = std::time::Instant::now();
            let words = heard_letters(&em, frames, seconds.parse().map_err(|_| "seconds")?, 0.0);
            let heard = HeardToks::of(&words, zh, Units::Letters);
            let bt = BookToks::of(&sections, zh, Units::Letters);
            let ix = KIndex::build(&bt.toks, Units::Letters.k(zh));
            let kind = best([&ix], &heard.toks);
            println!("# heard {} letters; match {:?}", heard.toks.len(), kind.map(|(_, r, k)| (r, k)));
            let c = coarse_in(&bt, &heard, &[(0, heard.toks.len())], zh, Units::Letters);
            for s in &c.sentences {
                let class = match s.class { Class::Aligned => "aligned", Class::Weak => "weak", Class::Unread => "unread" };
                println!("{}\t{}\t{}\t{:.3}\t{:.3}\t{:.2}", s.section, s.sentence, class, s.t0, s.t1, s.coverage);
            }
            for e in &c.extras {
                println!("# extra {:.2}-{:.2}", e.t0, e.t1);
            }
            println!("# {:?}", t.elapsed());
            Ok(())
        }
        ["lines", library, book, section] => {
            let core = open(library)?;
            let id = find(&core, book)?.id;
            let n = number(section)?;
            let sentences = core.sentences(id.clone(), n).map_err(err)?;
            for l in core.section_script(id, n).map_err(err)?.lines {
                let quote: String = sentences[l.first as usize].chars().skip(l.start as usize).take((l.end - l.start) as usize).take(30).collect();
                println!("{}\t{}\t{:?}\t{quote}", l.first, l.who.as_deref().unwrap_or("?"), l.how);
            }
            Ok(())
        }
        ["lines-json", library, book, section] => {
            // 剧本 as the reader's setBubbles takes it (without colours), for tests.
            let core = open(library)?;
            let id = find(&core, book)?.id;
            let lines: Vec<String> = core
                .section_script(id, number(section)?)
                .map_err(err)?
                .lines
                .into_iter()
                .map(|l| {
                    let who = l.who.map(|w| format!("{w:?}")).unwrap_or_else(|| "null".into());
                    format!(r#"{{"first":{},"last":{},"start":{},"end":{},"who":{who}}}"#, l.first, l.last, l.start, l.end)
                })
                .collect();
            println!("[{}]", lines.join(","));
            Ok(())
        }
        ["pinyin", file] => {
            for line in fs::read_to_string(file).map_err(|e| e.to_string())?.lines() {
                println!("{}", yuyue_core::reading::for_alignment(line).into_iter().map(|s| s.pinyin).collect::<Vec<_>>().join(" "));
            }
            Ok(())
        }
        ["fa", tsv, lang, rest @ ..] if rest.len() >= 2 && rest.len() % 2 == 0 => {
            // Sentences as `yuyue sentences` prints them; a line starting a chapter (Chapter I.)
            // starts a new section, so its title gets the wildcard around it.
            let mut book: Vec<(u32, Vec<String>)> = Vec::new();
            let mut keys = Vec::new();
            for line in fs::read_to_string(tsv).map_err(|e| e.to_string())?.lines() {
                let Some((k, text)) = line.split_once('\t') else { continue };
                // A title: Chapter I., or a short Chinese line with no punctuation (孔乙己).
                let short = text.chars().count() <= 6 && !text.contains(['，', '。', '？', '！', '︰', '：', '“', '”']) && text.chars().any(|c| ('\u{4e00}'..='\u{9fff}').contains(&c));
                let chapter = text.to_lowercase().starts_with("chapter ") || short;
                if book.is_empty() || chapter {
                    book.push((book.len() as u32, Vec::new()));
                }
                book.last_mut().unwrap().1.push(text.to_string());
                keys.push(k.to_string());
            }
            // One or more files, each `<emissions.f32> <seconds>`.
            let classes = yuyue_core::forced::LETTERS.chars().count();
            let mut files = Vec::new();
            for pair in rest.chunks(2) {
                let bytes = fs::read(pair[0]).map_err(|e| e.to_string())?;
                let em: Vec<f32> = bytes.as_chunks::<4>().0.iter().map(|c| f32::from_le_bytes([c[0], c[1], c[2], c[3]])).collect();
                let frames = em.len() / classes;
                let seconds: f64 = pair[1].parse().map_err(|_| "seconds".to_string())?;
                files.push(yuyue_core::forced::FileEmissions { em, frames, seconds });
            }
            let frames: usize = files.iter().map(|f| f.frames).sum();
            let started = std::time::Instant::now();
            let out = yuyue_core::forced::align_files(&book, *lang == "zh", &files);
            eprintln!("{} frames, {:.1} s", frames, started.elapsed().as_secs_f64());
            for (k, t) in keys.iter().zip(out.sentences) {
                println!("{k}\t{:.2}\t{:.2}\t{:.2}", t.start, t.end, t.heard);
            }
            Ok(())
        }
        ["digest", file] => {
            println!("{}", yuyue_core::forced::file_digest(file.to_string()).map_err(err)?);
            Ok(())
        }
        ["marks", file] => {
            for m in yuyue_core::m4b::chapter_marks(Path::new(file)).map_err(err)? {
                println!("{:.2}\t{}", m.start, m.title.unwrap_or_default());
            }
            Ok(())
        }
        ["site", library, out] => site(&open(library)?, Path::new(library), Path::new(out), app.as_deref(), &geometry),
        _ => Err(USAGE.to_string()),
    }
}

fn err(e: CoreError) -> String {
    e.to_string()
}

fn number(s: &str) -> Result<u32, String> {
    s.parse().map_err(|_| format!("not a section number: {s}"))
}

fn find(core: &Arc<YuyueCore>, prefix: &str) -> Result<BookSummary, String> {
    let mut matches: Vec<_> = core.books().map_err(err)?.into_iter().filter(|b| b.id.starts_with(prefix)).collect();
    match matches.len() {
        1 => Ok(matches.remove(0)),
        0 => Err(format!("no book {prefix}")),
        _ => Err(format!("{prefix} matches more than one book")),
    }
}

fn site(core: &Arc<YuyueCore>, library: &Path, out: &Path, app: Option<&str>, geometry: &PageGeometry) -> Result<(), String> {
    let io = |e: std::io::Error| e.to_string();
    let mut index = String::from(
        "<!DOCTYPE html>\n<html lang=\"zh-CN\"><meta charset=\"utf-8\"><title>语阅 · 书页</title>\n<body style=\"font:16px/1.6 system-ui;margin:2em\">\n<h1>书页</h1>\n<ul>\n",
    );
    for b in core.books().map_err(err)? {
        let detail = core.book(b.id.clone()).map_err(err)?;
        let dir = out.join("book").join(&b.id);
        fs::create_dir_all(dir.join("s")).map_err(io)?;
        for n in 0..detail.sections.len() as u32 {
            let html = core.section_document(b.id.clone(), n, geometry.clone()).map_err(err)?;
            fs::write(dir.join("s").join(format!("{n}.html")), html).map_err(io)?;
        }
        let resources = library.join("books").join(&b.id).join("r");
        if resources.is_dir() {
            copy_dir(&resources, &dir.join("r")).map_err(io)?;
        }
        let _ = write!(index, "<li><a href=\"book/{}/s/{}.html\">{}</a> <small>", b.id, detail.start, escape(&b.title));
        for e in detail.toc.iter().filter(|e| e.depth == 0).take(200) {
            let _ = write!(index, " · <a href=\"book/{}/s/{}.html\">{}</a>", b.id, e.section, escape(&e.title));
        }
        index.push_str("</small></li>\n");
        println!("{}  {}", b.id, b.title);
    }
    index.push_str("</ul>\n</body>\n</html>\n");
    fs::write(out.join("index.html"), index).map_err(io)?;
    if let Some(app) = app {
        copy_dir(Path::new(app), &out.join("app")).map_err(io)?;
    }
    Ok(())
}

fn copy_dir(from: &Path, to: &Path) -> std::io::Result<()> {
    fs::create_dir_all(to)?;
    for entry in fs::read_dir(from)? {
        let entry = entry?;
        let target = to.join(entry.file_name());
        if entry.file_type()?.is_dir() {
            copy_dir(&entry.path(), &target)?;
        } else {
            fs::copy(entry.path(), target)?;
        }
    }
    Ok(())
}

fn escape(s: &str) -> String {
    s.replace('&', "&amp;").replace('<', "&lt;").replace('>', "&gt;")
}

/// A WAV's samples (16-bit or 32-bit float, channels averaged) and rate.
fn sense_voice(meta: &str, tokens: &str) -> Result<std::sync::Arc<yuyue_core::sensevoice::SenseVoice>, String> {
    let meta: std::collections::HashMap<String, String> = serde_json::from_slice(&fs::read(meta).map_err(|e| e.to_string())?).map_err(|e| e.to_string())?;
    yuyue_core::sensevoice::SenseVoice::new(meta, fs::read_to_string(tokens).map_err(|e| e.to_string())?).map_err(|e| e.to_string())
}

fn read_wav(b: &[u8]) -> Option<(Vec<f32>, u32)> {
    if b.len() < 12 || &b[0..4] != b"RIFF" || &b[8..12] != b"WAVE" {
        return None;
    }
    let u16_at = |i: usize| u16::from_le_bytes([b[i], b[i + 1]]);
    let u32_at = |i: usize| u32::from_le_bytes([b[i], b[i + 1], b[i + 2], b[i + 3]]);
    let (mut format, mut channels, mut rate, mut bits) = (1u16, 1usize, 16_000u32, 16usize);
    let mut at = 12;
    while at + 8 <= b.len() {
        let size = u32_at(at + 4) as usize;
        let body = at + 8;
        match &b[at..at + 4] {
            b"fmt " => {
                format = u16_at(body);
                channels = u16_at(body + 2).max(1) as usize;
                rate = u32_at(body + 4);
                bits = u16_at(body + 14) as usize;
            }
            b"data" => {
                let end = b.len().min(body + size);
                let step = bits / 8 * channels;
                let n = (end - body) / step;
                let out = (0..n)
                    .map(|i| {
                        (0..channels)
                            .map(|c| {
                                let p = body + i * step + c * bits / 8;
                                if format == 3 && bits == 32 { f32::from_le_bytes([b[p], b[p + 1], b[p + 2], b[p + 3]]) } else { i16::from_le_bytes([b[p], b[p + 1]]) as f32 / 32768.0 }
                            })
                            .sum::<f32>()
                            / channels as f32
                    })
                    .collect();
                return Some((out, rate));
            }
            _ => {}
        }
        at = body + size + (size & 1);
    }
    None
}
