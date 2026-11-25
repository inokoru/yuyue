using System.Text.Json;
using Xunit;
using Yuyue.Core;

namespace Yuyue.Tests;

/// <summary>
/// Layer 2: Shared UniFFI conformance runner (Windows / C# side).
/// Executes the exact same <c>fixtures/conformance/*.json</c> scenarios as Rust, Swift, and Kotlin.
/// </summary>
public sealed class ConformanceTests
{
    public static IEnumerable<object[]> ConformanceCases()
    {
        string dir = TestPaths.Fixture("conformance");
        foreach (string file in Directory.GetFiles(dir, "*.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            if (Path.GetFileName(file).Equals("reader-messages.json", StringComparison.OrdinalIgnoreCase))
                continue;
            yield return [Path.GetFileName(file)];
        }
    }

    [Theory]
    [MemberData(nameof(ConformanceCases))]
    public void SharedConformanceScenario_Passes(string fileName)
    {
        string fullPath = TestPaths.Fixture("conformance", fileName);
        using var doc = JsonDocument.Parse(File.ReadAllText(fullPath));
        var root = doc.RootElement;
        string id = root.GetProperty("id").GetString()!;
        var steps = root.GetProperty("steps");

        string work = Path.Combine(Path.GetTempPath(), $"yuyue-cs-conformance-{id}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);

        var cores = new Dictionary<string, YuyueCore>(StringComparer.Ordinal);
        var vars = new Dictionary<string, string>(StringComparer.Ordinal);
        var geom = new PageGeometry(Paging.Native, 20.0, 50.0, 80.0, 1, false);

        try
        {
            int idx = 0;
            foreach (var step in steps.EnumerateArray())
            {
                string op = step.GetProperty("op").GetString()!;
                string ctx = $"{id}[{idx}] ({op})";
                switch (op)
                {
                    case "open":
                    {
                        string dev = step.GetProperty("device").GetString()!;
                        string name = step.GetProperty("name").GetString()!;
                        string platform = step.GetProperty("platform").GetString()!;
                        string dir = Path.Combine(work, $"dev-{dev}");
                        Directory.CreateDirectory(dir);
                        if (cores.TryGetValue(dev, out var existing))
                        {
                            existing.Dispose();
                            cores.Remove(dev);
                        }
                        cores[dev] = YuyueCore.Open(dir, name, platform);
                        break;
                    }
                    case "close":
                    {
                        string dev = step.GetProperty("device").GetString()!;
                        if (cores.TryGetValue(dev, out var existing))
                        {
                            existing.Dispose();
                            cores.Remove(dev);
                        }
                        break;
                    }
                    case "import_book":
                    {
                        var core = cores[step.GetProperty("device").GetString()!];
                        string rel = step.GetProperty("fixture").GetString()!;
                        string fixturePath = TestPaths.Fixture(rel.Split('/'));
                        bool expectError = step.TryGetProperty("expect_error", out var ee) && ee.GetBoolean();
                        if (expectError)
                        {
                            Assert.ThrowsAny<Exception>(() => core.ImportBook(fixturePath));
                            break;
                        }
                        var book = core.ImportBook(fixturePath);
                        if (step.TryGetProperty("save_as", out var saveProp) && saveProp.GetString() is string saveKey)
                            vars[saveKey] = book.Id;
                        if (step.TryGetProperty("expect", out var exp))
                        {
                            if (exp.TryGetProperty("format", out var fmt))
                                Assert.Equal(ParseFormat(fmt.GetString()!), book.Format);
                            if (exp.TryGetProperty("available", out var avail))
                                Assert.Equal(avail.GetBoolean(), book.Available);
                            if (exp.TryGetProperty("same_id_as", out var sameRef))
                                Assert.Equal(vars[sameRef.GetString()!], book.Id);
                            if (exp.TryGetProperty("title_contains", out var tc))
                                Assert.Contains(tc.GetString()!, book.Title);
                        }
                        break;
                    }
                    case "assert_books":
                    {
                        var core = cores[step.GetProperty("device").GetString()!];
                        var books = core.Books();
                        if (step.TryGetProperty("count", out var cnt))
                            Assert.Equal(cnt.GetInt32(), books.Length);
                        if (step.TryGetProperty("first_id_ref", out var firstRef))
                            Assert.Equal(vars[firstRef.GetString()!], books[0].Id);
                        break;
                    }
                    case "assert_sentences":
                    {
                        var core = cores[step.GetProperty("device").GetString()!];
                        string bookId = vars[step.GetProperty("book").GetString()!];
                        uint section = step.GetProperty("section").GetUInt32();
                        string[] sents = core.Sentences(bookId, section);
                        if (step.TryGetProperty("min_count", out var minC))
                            Assert.True(sents.Length >= minC.GetInt32(), $"{ctx}: sentences len {sents.Length}");
                        if (step.TryGetProperty("any_contains", out var sub))
                            Assert.Contains(sents, s => s.Contains(sub.GetString()!, StringComparison.Ordinal));
                        break;
                    }
                    case "save_progress":
                    {
                        var core = cores[step.GetProperty("device").GetString()!];
                        string bookId = vars[step.GetProperty("book").GetString()!];
                        core.SaveProgress(bookId, new Position(
                            step.GetProperty("section").GetUInt32(),
                            step.GetProperty("sentence").GetUInt32()));
                        break;
                    }
                    case "assert_progress":
                    {
                        var core = cores[step.GetProperty("device").GetString()!];
                        string bookId = vars[step.GetProperty("book").GetString()!];
                        string kind = step.GetProperty("kind").GetString()!;
                        var got = kind == "own" ? core.Progress(bookId) : core.LatestProgress(bookId);
                        bool present = step.GetProperty("present").GetBoolean();
                        Assert.Equal(present, got is not null);
                        if (got is not null)
                        {
                            if (step.TryGetProperty("section", out var sec))
                                Assert.Equal(sec.GetUInt32(), got.Position.Section);
                            if (step.TryGetProperty("sentence", out var sen))
                                Assert.Equal(sen.GetUInt32(), got.Position.Sentence);
                            if (step.TryGetProperty("this_device", out var td))
                                Assert.Equal(td.GetBoolean(), got.ThisDevice);
                            if (step.TryGetProperty("min_fraction", out var mf))
                                Assert.True(got.Fraction >= mf.GetDouble(), $"{ctx}: fraction {got.Fraction}");
                        }
                        break;
                    }
                    case "set_reader_settings":
                    {
                        var core = cores[step.GetProperty("device").GetString()!];
                        var s = core.ReaderSettings();
                        if (step.TryGetProperty("paper", out var p)) s = s with { Paper = ParsePaper(p.GetString()!) };
                        if (step.TryGetProperty("mode", out var m)) s = s with { Mode = ParseMode(m.GetString()!) };
                        if (step.TryGetProperty("font", out var f)) s = s with { Font = ParseFont(f.GetString()!) };
                        if (step.TryGetProperty("font_size", out var fs)) s = s with { FontSize = fs.GetDouble() };
                        if (step.TryGetProperty("compress", out var c)) s = s with { Compress = c.GetBoolean() };
                        core.SetReaderSettings(s);
                        break;
                    }
                    case "assert_reader_settings":
                    {
                        var core = cores[step.GetProperty("device").GetString()!];
                        var s = core.ReaderSettings();
                        if (step.TryGetProperty("paper", out var p)) Assert.Equal(ParsePaper(p.GetString()!), s.Paper);
                        if (step.TryGetProperty("mode", out var m)) Assert.Equal(ParseMode(m.GetString()!), s.Mode);
                        if (step.TryGetProperty("font", out var f)) Assert.Equal(ParseFont(f.GetString()!), s.Font);
                        if (step.TryGetProperty("font_size", out var fs)) Assert.Equal(fs.GetDouble(), s.FontSize, 6);
                        if (step.TryGetProperty("compress", out var c)) Assert.Equal(c.GetBoolean(), s.Compress);
                        break;
                    }
                    case "set_book_reader_settings":
                    {
                        var core = cores[step.GetProperty("device").GetString()!];
                        string bookId = vars[step.GetProperty("book").GetString()!];
                        if (step.TryGetProperty("clear", out var clr) && clr.GetBoolean())
                        {
                            core.SetBookReaderSettings(bookId, null);
                        }
                        else
                        {
                            var s = core.ReaderSettings();
                            if (step.TryGetProperty("paper", out var p)) s = s with { Paper = ParsePaper(p.GetString()!) };
                            if (step.TryGetProperty("font_size", out var fs)) s = s with { FontSize = fs.GetDouble() };
                            core.SetBookReaderSettings(bookId, s);
                        }
                        break;
                    }
                    case "assert_section_document":
                    {
                        var core = cores[step.GetProperty("device").GetString()!];
                        string bookId = vars[step.GetProperty("book").GetString()!];
                        uint section = step.GetProperty("section").GetUInt32();
                        string html = core.SectionDocument(bookId, section, geom);
                        foreach (var needle in step.GetProperty("contains").EnumerateArray())
                            Assert.Contains(needle.GetString()!, html, StringComparison.Ordinal);
                        break;
                    }
                    case "add_mark":
                    {
                        var core = cores[step.GetProperty("device").GetString()!];
                        string bookId = vars[step.GetProperty("book").GetString()!];
                        string? note = step.TryGetProperty("note", out var np) && np.ValueKind == JsonValueKind.String ? np.GetString() : null;
                        var mark = core.AddMark(
                            bookId,
                            step.GetProperty("section").GetUInt32(),
                            new TextPoint(step.GetProperty("start_sentence").GetUInt32(), step.GetProperty("start_offset").GetUInt32()),
                            new TextPoint(step.GetProperty("end_sentence").GetUInt32(), step.GetProperty("end_offset").GetUInt32()),
                            step.GetProperty("text").GetString()!,
                            ParseStyle(step.GetProperty("style").GetString()!),
                            note);
                        if (step.TryGetProperty("save_as", out var saveProp) && saveProp.GetString() is string saveKey)
                            vars[saveKey] = mark.Id;
                        break;
                    }
                    case "set_mark_note":
                    {
                        var core = cores[step.GetProperty("device").GetString()!];
                        string bookId = vars[step.GetProperty("book").GetString()!];
                        string markId = vars[step.GetProperty("mark").GetString()!];
                        string? note = step.TryGetProperty("note", out var np) && np.ValueKind == JsonValueKind.String ? np.GetString() : null;
                        core.SetMarkNote(bookId, markId, note);
                        break;
                    }
                    case "set_mark_style":
                    {
                        var core = cores[step.GetProperty("device").GetString()!];
                        string bookId = vars[step.GetProperty("book").GetString()!];
                        string markId = vars[step.GetProperty("mark").GetString()!];
                        core.SetMarkStyle(bookId, markId, ParseStyle(step.GetProperty("style").GetString()!));
                        break;
                    }
                    case "assert_marks":
                    {
                        var core = cores[step.GetProperty("device").GetString()!];
                        string bookId = vars[step.GetProperty("book").GetString()!];
                        var marks = core.Marks(bookId);
                        int count = step.GetProperty("count").GetInt32();
                        Assert.Equal(count, marks.Length);
                        if (count > 0)
                        {
                            if (step.TryGetProperty("first_style", out var fs))
                                Assert.Equal(ParseStyle(fs.GetString()!), marks[0].Style);
                            if (step.TryGetProperty("first_note", out var fn))
                                Assert.Equal(fn.GetString(), marks[0].Note);
                        }
                        break;
                    }
                    case "search_book":
                    {
                        var core = cores[step.GetProperty("device").GetString()!];
                        string bookId = vars[step.GetProperty("book").GetString()!];
                        var hits = core.SearchBook(bookId, step.GetProperty("query").GetString()!);
                        Assert.True(hits.Length >= step.GetProperty("min_hits").GetInt32(), $"{ctx}: hits {hits.Length}");
                        break;
                    }
                    case "delete_book":
                    {
                        var core = cores[step.GetProperty("device").GetString()!];
                        core.DeleteBook(vars[step.GetProperty("book").GetString()!]);
                        break;
                    }
                    case "restore_book":
                    {
                        var core = cores[step.GetProperty("device").GetString()!];
                        core.RestoreBook(vars[step.GetProperty("book").GetString()!]);
                        break;
                    }
                    case "sleep_ms":
                    {
                        Thread.Sleep(step.GetProperty("ms").GetInt32());
                        break;
                    }
                    case "sync":
                    {
                        var from = cores[step.GetProperty("from").GetString()!];
                        var to = cores[step.GetProperty("to").GetString()!];
                        bool full = step.TryGetProperty("full", out var fp) && fp.GetBoolean();
                        string ver = full ? "{}" : to.SyncVersion();
                        SyncPart[] leftOut = step.TryGetProperty("left_out", out var lo)
                            ? lo.EnumerateArray().Select(e => ParseSyncPart(e.GetString()!)).ToArray()
                            : [];
                        string batch = leftOut.Length == 0 ? from.ExportChanges(ver) : from.ExportChangesExcept(ver, leftOut);
                        uint changed = leftOut.Length == 0 ? to.ImportChanges(batch) : to.ImportChangesExcept(batch, leftOut);
                        if (step.TryGetProperty("min_changed", out var mc))
                            Assert.True(changed >= mc.GetUInt32(), $"{ctx}: changed {changed}");
                        if (step.TryGetProperty("exact_changed", out var ec))
                            Assert.Equal(ec.GetUInt32(), changed);
                        break;
                    }
                    case "assert_conflicts":
                    {
                        var core = cores[step.GetProperty("device").GetString()!];
                        var conflicts = core.NoteConflicts();
                        int count = step.GetProperty("count").GetInt32();
                        Assert.Equal(count, conflicts.Length);
                        if (count > 0)
                        {
                            if (step.TryGetProperty("first_older_note", out var on))
                                Assert.Equal(on.GetString(), conflicts[0].Older.Note);
                            if (step.TryGetProperty("first_newer_note", out var nn))
                                Assert.Equal(nn.GetString(), conflicts[0].Newer.Note);
                        }
                        break;
                    }
                    case "resolve_conflict":
                    {
                        var core = cores[step.GetProperty("device").GetString()!];
                        core.ResolveNoteConflict(
                            vars[step.GetProperty("book").GetString()!],
                            vars[step.GetProperty("mark").GetString()!],
                            ParseChoice(step.GetProperty("choice").GetString()!));
                        break;
                    }
                    case "make_snapshot":
                    {
                        var core = cores[step.GetProperty("device").GetString()!];
                        string snap = Path.Combine(work, step.GetProperty("snapshot_name").GetString()!);
                        ulong bytes = core.MakeSnapshot(snap, new SnapshotParts(
                            step.GetProperty("progress").GetBoolean(),
                            step.GetProperty("notes").GetBoolean(),
                            step.GetProperty("words").GetBoolean(),
                            step.GetProperty("settings").GetBoolean(),
                            step.GetProperty("files").GetBoolean()));
                        Assert.True(bytes > 0, $"{ctx}: empty snapshot");
                        break;
                    }
                    case "restore_snapshot":
                    {
                        var core = cores[step.GetProperty("device").GetString()!];
                        string snap = Path.Combine(work, step.GetProperty("snapshot_name").GetString()!);
                        core.RestoreSnapshot(snap, step.GetProperty("replace").GetBoolean());
                        break;
                    }
                    case "assert_snapshot_counts":
                    {
                        var core = cores[step.GetProperty("device").GetString()!];
                        var counts = core.SnapshotCounts();
                        if (step.TryGetProperty("min_books", out var mb))
                            Assert.True(counts.Books >= mb.GetUInt32(), $"{ctx}: books {counts.Books}");
                        if (step.TryGetProperty("min_notes", out var mn))
                            Assert.True(counts.Notes >= mn.GetUInt32(), $"{ctx}: notes {counts.Notes}");
                        break;
                    }
                    case "assert_page_sentences":
                    {
                        var got = YuyueCoreMethods.PageSentences(step.GetProperty("text").GetString()!)
                            .Select(s => s.Text)
                            .ToArray();
                        var exp = step.GetProperty("expected_texts").EnumerateArray().Select(e => e.GetString()!).ToArray();
                        Assert.Equal(exp, got);
                        break;
                    }
                    case "assert_compare_versions":
                    {
                        foreach (var c in step.GetProperty("cases").EnumerateArray())
                        {
                            int got = Math.Sign(YuyueCoreMethods.CompareVersions(
                                c.GetProperty("a").GetString()!,
                                c.GetProperty("b").GetString()!));
                            Assert.Equal(c.GetProperty("sign").GetInt32(), got);
                        }
                        break;
                    }
                    default:
                        throw new InvalidOperationException($"{ctx}: unsupported op '{op}'");
                }
                idx++;
            }
        }
        finally
        {
            foreach (var c in cores.Values)
            {
                try { c.Dispose(); } catch { }
            }
            try { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); } catch { }
        }
    }

    private static Paper ParsePaper(string s) => s switch
    {
        "plain" => Paper.Plain,
        "dark" => Paper.Dark,
        "kami" => Paper.Kami,
        "kaminight" => Paper.KamiNight,
        "sage" => Paper.Sage,
        "eink" => Paper.Eink,
        _ => throw new ArgumentOutOfRangeException(nameof(s), s, null),
    };

    private static LayoutMode ParseMode(string s) => s switch
    {
        "paginated" => LayoutMode.Paginated,
        "scrolled" => LayoutMode.Scrolled,
        _ => throw new ArgumentOutOfRangeException(nameof(s), s, null),
    };

    private static Font ParseFont(string s) => s switch
    {
        "sans" => Font.Sans,
        "serif" => Font.Serif,
        "kai" => Font.Kai,
        "wenkai" => Font.WenKai,
        _ => throw new ArgumentOutOfRangeException(nameof(s), s, null),
    };

    private static MarkStyle ParseStyle(string s) => s switch
    {
        "yellow" => MarkStyle.Yellow,
        "blue" => MarkStyle.Blue,
        "pink" => MarkStyle.Pink,
        "wave" => MarkStyle.Wave,
        "underline" => MarkStyle.Underline,
        "dotted" => MarkStyle.Dotted,
        _ => throw new ArgumentOutOfRangeException(nameof(s), s, null),
    };

    private static BookFormat ParseFormat(string s) => s switch
    {
        "epub" => BookFormat.Epub,
        "txt" => BookFormat.Txt,
        "pdf" => BookFormat.Pdf,
        "mobi" => BookFormat.Mobi,
        "audio" => BookFormat.Audio,
        "podcast" => BookFormat.Podcast,
        _ => throw new ArgumentOutOfRangeException(nameof(s), s, null),
    };

    private static SyncPart ParseSyncPart(string s) => s switch
    {
        "reading" => SyncPart.Reading,
        "notes" => SyncPart.Notes,
        "settings" => SyncPart.Settings,
        _ => throw new ArgumentOutOfRangeException(nameof(s), s, null),
    };

    private static ConflictChoice ParseChoice(string s) => s switch
    {
        "older" => ConflictChoice.Older,
        "newer" => ConflictChoice.Newer,
        "both" => ConflictChoice.Both,
        _ => throw new ArgumentOutOfRangeException(nameof(s), s, null),
    };
}
