using Xunit;
using Yuyue.Core;
using Yuyue.Speech;

namespace Yuyue.Tests;

/// <summary>
/// Unit &amp; integration tests verifying every Windows parity gap closed in this milestone:
/// reading heatmap math, daily review, sub-item undo, word count, record-only + purge,
/// and drama/audiobook UniFFI facades.
/// </summary>
public sealed class ParityGapClosureTests
{
    [Fact]
    public void ReadingHeatmapBuilder_AlignsWeeksOnMondayAndComputesStreaks()
    {
        // 2026-03-11 is a Wednesday
        var today = new DateOnly(2026, 3, 11);
        DayTime[] times =
        [
            new("2026-03-08", "b1", 600, 0),    // Sunday -> level 1 (<15m)
            new("2026-03-09", "b1", 1200, 0),   // Monday -> level 2 (20m)
            new("2026-03-09", "b2", 0, 600),    // Same day listen -> total 30m -> level 3
            new("2026-03-10", "b1", 3600, 0),   // Tuesday -> level 4 (60m)
        ];

        var summary = ReadingHeatmapBuilder.Build(times, today, weekCount: 4);

        Assert.Equal(4, summary.Weeks.Count);
        foreach (var week in summary.Weeks)
        {
            Assert.Equal(7, week.Count);
            Assert.Equal(DayOfWeek.Monday, week[0].Date.DayOfWeek);
            Assert.Equal(DayOfWeek.Sunday, week[6].Date.DayOfWeek);
        }

        Assert.Equal(3, summary.ActiveDays);
        Assert.Equal(5400u, summary.TotalReadSeconds);
        Assert.Equal(600u, summary.TotalListenSeconds);
        Assert.Equal(3, summary.LongestStreak);
        // Today (Wed) has 0s, so current streak falls back to yesterday (Tue -> Mon -> Sun = 3)
        Assert.Equal(3, summary.CurrentStreak);

        var lastWeek = summary.Weeks[^1];
        Assert.Equal(3, lastWeek[0].Level); // Mon 2026-03-09 = 30m -> level 3
        Assert.Equal(4, lastWeek[1].Level); // Tue 2026-03-10 = 60m -> level 4
        Assert.Equal(0, lastWeek[2].Level); // Wed 2026-03-11 = 0m -> level 0
        Assert.False(lastWeek[2].IsFuture);
        Assert.True(lastWeek[3].IsFuture);  // Thu 2026-03-12 is future
    }

    [Fact]
    public void LibraryService_FacadeClosesAllCoreShelfReviewAndDramaGaps()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"yuyue-parity-gap-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            using var core = YuyueCore.Open(dir, "Win-Test", "windows");
            var epub1 = core.ImportBook(TestPaths.Fixture("books", "beiying.epub"));
            var epub2 = core.ImportBook(TestPaths.Fixture("books", "pride-and-prejudice.epub"));

            // 1. Daily review
            var mark = core.AddMark(epub1.Id, 0, new TextPoint(0, 0), new TextPoint(0, 4), "我与父亲", MarkStyle.Yellow, "批注");
            var deck0 = core.ReviewToday(null, 0, 5);
            Assert.Single(deck0.Items);
            Assert.Equal(0u, deck0.DoneToday);

            core.SetMarkReview(epub1.Id, mark.Id, keep: true);
            var deck1 = core.ReviewToday(null, 0, 5);
            Assert.Empty(deck1.Items);
            Assert.Equal(1u, deck1.DoneToday);

            // 2. Reading stats DayTimes
            core.AddTime(epub1.Id, 0, 1200, 300, "2026-03-10");
            var dt = core.DayTimes("2026-03-01", "2026-03-31");
            Assert.Contains(dt, r => r.Day == "2026-03-10" && r.ReadSeconds == 1200 && r.ListenSeconds == 300);

            // 3. Sub-item undo (bookmark, collection, custom paper, word) + book word count
            var bm = core.AddBookmark(epub1.Id, 0, 1);
            core.DeleteBookmark(epub1.Id, bm.Id);
            Assert.Empty(core.Bookmarks(epub1.Id));
            core.RestoreBookmark(epub1.Id, bm.Id);
            Assert.Single(core.Bookmarks(epub1.Id));

            var col = core.CreateCollection("现代散文", null, epub1.Id);
            core.DeleteCollection(col.Id);
            Assert.DoesNotContain(core.Collections(), c => c.Id == col.Id);
            core.RestoreCollection(col.Id);
            Assert.Contains(core.Collections(), c => c.Id == col.Id);

            var paper = core.SaveCustomPaper(new CustomPaper("", "米黄", "#F6F1E6", null, Paper.Kami, PaperTexture.None));
            core.DeleteCustomPaper(paper.Id);
            Assert.DoesNotContain(core.CustomPapers(), p => p.Id == paper.Id);
            core.RestoreCustomPaper(paper.Id);
            Assert.Contains(core.CustomPapers(), p => p.Id == paper.Id);

            core.AddWord("Bennet", null, "贝内特", epub2.Id, 0, 0, "Mr. Bennet");
            Assert.True(core.BookWordCount(epub2.Id, "Bennet") >= 1u);
            core.DeleteWord("Bennet");
            Assert.DoesNotContain(core.Words(), w => w.Word.Equals("Bennet", StringComparison.OrdinalIgnoreCase));
            core.RestoreWord("Bennet");
            Assert.Contains(core.Words(), w => w.Word.Equals("Bennet", StringComparison.OrdinalIgnoreCase));

            // 4. Drama & Audiobook non-destructive queries (while book file is still present)
            Assert.False(core.AudiobookIsTextVersion(epub1.Id));
            core.DramaSetMode(epub1.Id, true);
            _ = core.DramaCanAct(epub1.Id);
            _ = core.DramaSectionLayer(epub1.Id, 0, "zipvoice");
            _ = core.SectionRoles(epub1.Id, 0);
            Assert.Null(core.DramaBorrowedFrom(epub1.Id));

            // 5. Finished + Record-only + PurgeDeleted
            core.SaveProgress(epub1.Id, new Position(0, 1));
            core.SetFinished(epub1.Id, true);
            Assert.Equal(ReadingStatus.Finished, core.Books().First(b => b.Id == epub1.Id).Status);
            core.SetFinished(epub1.Id, false);
            Assert.Equal(ReadingStatus.Reading, core.Books().First(b => b.Id == epub1.Id).Status);
            var recOnly = core.KeepRecordOnly(epub1.Id);
            Assert.True(recOnly.Record);
            Assert.False(recOnly.Available);

            core.DeleteBook(epub2.Id);
            Assert.True(core.PurgeDeleted(0) >= 1u);
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
