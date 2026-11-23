using System.Net;
using Xunit;
using Yuyue.Core;
using Yuyue.Pdf;
using Yuyue.Settings;

namespace Yuyue.Tests;

public sealed class WaveDParityTests
{
    [Fact]
    public void NameMatcher_MatchesMandarinPinyinInitialsAndEnglishWords()
    {
        Assert.Equal("st", NameMatcher.ExtractInitials("三体"));
        Assert.Equal("hlm", NameMatcher.ExtractInitials("红楼梦"));
        Assert.Equal("bngd", NameMatcher.ExtractInitials("百年孤独"));
        Assert.Equal("hp", NameMatcher.ExtractInitials("Harry Potter"));

        Assert.True(NameMatcher.Matches("三体", "st"));
        Assert.True(NameMatcher.Matches("红楼梦", "hlm"));
        Assert.True(NameMatcher.Matches("百年孤独", "bn gd"));
        Assert.True(NameMatcher.Matches("Harry Potter and the Philosopher's Stone", "hp"));
        Assert.True(NameMatcher.Matches("红楼梦", "楼梦"));
        Assert.False(NameMatcher.Matches("红楼梦", "xyz"));
        Assert.False(NameMatcher.Matches("三体", "hlm"));
    }

    [Fact]
    public void PdfInfoReader_SanitizesJunkMetadataAndDetectsLanguage()
    {
        Assert.Null(PdfInfoReader.SanitizePdfTitle("Microsoft Word - chapter1.docx"));
        Assert.Null(PdfInfoReader.SanitizePdfTitle("Untitled"));
        Assert.Null(PdfInfoReader.SanitizePdfTitle("report.pdf"));
        Assert.Equal("背影", PdfInfoReader.SanitizePdfTitle("  背影  "));

        Assert.Null(PdfInfoReader.SanitizePdfAuthor("Administrator"));
        Assert.Null(PdfInfoReader.SanitizePdfAuthor("User"));
        Assert.Equal("朱自清", PdfInfoReader.SanitizePdfAuthor("朱自清"));

        Assert.Equal("zh-Hans", PdfInfoReader.DetectLanguage("我与父亲不相见已二年余了，我最不能忘记的是他的背影。"));
        Assert.Equal("en-US", PdfInfoReader.DetectLanguage("In my younger and more vulnerable years my father gave me some advice."));
    }

    [Fact]
    public void WatchedFolder_PersistsFingerprintsAndDetectsModification()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "yuyue-test-watch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string store = Path.Combine(tempDir, "watched-seen.json");
            string file = Path.Combine(tempDir, "sub", "book.epub");
            string fp1 = WatchedFolderService.ComputeFingerprint(file, 1024, 638000000000000000L);
            string fp2 = WatchedFolderService.ComputeFingerprint(file, 2048, 638000000000000000L);
            string fp3 = WatchedFolderService.ComputeFingerprint(file, 1024, 638000000010000000L);

            Assert.NotEqual(fp1, fp2);
            Assert.NotEqual(fp1, fp3);

            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { fp1 };
            WatchedFolderService.SaveFingerprints(store, set);

            var reloaded = WatchedFolderService.LoadFingerprints(store);
            Assert.Contains(fp1, reloaded);
            Assert.DoesNotContain(fp2, reloaded);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void WifiTransfer_KeepsCleanFilenameAndValidatesChunkOffsets()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "yuyue-test-wifi-" + Guid.NewGuid().ToString("N"));
        string resolved = WifiTransferService.ResolveUploadPath(tempRoot, "abc123", "../subdir/三体.epub");

        Assert.Equal("三体.epub", Path.GetFileName(resolved));
        Assert.Equal("abc123", Path.GetFileName(Path.GetDirectoryName(resolved)));
        Assert.Throws<ArgumentException>(() => WifiTransferService.ResolveUploadPath(tempRoot, "../escape", "book.epub"));

        var exact = WifiTransferService.ValidateChunkOffset(4096, 4096);
        Assert.True(exact.Accepted);
        Assert.Equal(200, exact.StatusCode);

        var retry = WifiTransferService.ValidateChunkOffset(0, 4096);
        Assert.True(retry.Accepted);
        Assert.Equal(4096, retry.ExpectedOffset);

        var sparseGap = WifiTransferService.ValidateChunkOffset(8192, 4096);
        Assert.False(sparseGap.Accepted);
        Assert.Equal(4096, sparseGap.ExpectedOffset);
        Assert.Equal(4096, WifiTransferService.ValidateChunkOffset(-1, 4096).ExpectedOffset);
        Assert.False(WifiTransferService.ValidateChunkOffset(-1, 4096).Accepted);
    }

    [Fact]
    public void SettingsHistoryStore_FoldsAdjacentEditsSupportsUndoRedoAndPersists()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "yuyue-test-hist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var store = new SettingsHistoryStore(tempDir);
            int fontSize = 18;
            var t0 = new DateTimeOffset(2026, 11, 23, 12, 0, 0, System.TimeSpan.Zero);

            store.Record("reading", "字号", "20", oldValue: "18", undo: () => fontSize = 18, redo: () => fontSize = 20, now: t0);
            fontSize = 20;
            // Rapid slider adjustment within 10s should fold into the same entry while updating Redo to 24
            store.Record("reading", "字号", "24", oldValue: "20", undo: () => fontSize = 20, redo: () => fontSize = 24, now: t0.AddSeconds(4));
            fontSize = 24;

            Assert.Single(store.Entries);
            Assert.Equal("18", store.Entries[0].OldValue);
            Assert.Equal("24", store.Entries[0].NewValue);
            Assert.Equal("阅读", store.Entries[0].Category);

            // Undo restores initial value 18 and enables Redo
            var undone = store.TryUndo();
            Assert.NotNull(undone);
            Assert.Equal(18, fontSize);
            Assert.True(store.CanRedo);

            // Redo restores folded final value 24
            var redone = store.TryRedo();
            Assert.NotNull(redone);
            Assert.Equal(24, fontSize);
            Assert.False(store.CanRedo);

            // New store instance loads persisted JSON history
            var reloaded = new SettingsHistoryStore(tempDir);
            Assert.Single(reloaded.Entries);
            Assert.Equal("字号", reloaded.Entries[0].Title);
            Assert.Equal("24", reloaded.Entries[0].NewValue);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void NoteExport_FormatsLocalizedHttpErrors()
    {
        Assert.Contains("401", NoteExportService.FormatHttpError("Notion", HttpStatusCode.Unauthorized));
        Assert.Contains("403", NoteExportService.FormatHttpError("Notion", HttpStatusCode.Forbidden));
        Assert.Contains("404", NoteExportService.FormatHttpError("Notion", HttpStatusCode.NotFound));
        Assert.Contains("429", NoteExportService.FormatHttpError("flomo", HttpStatusCode.TooManyRequests));
    }

    [Fact]
    public void FontShelfService_ImportsDeletesCustomFontsAndDiscoversSystemCjkFonts()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "yuyue-test-fonts-" + Guid.NewGuid().ToString("N"));
        string srcFile = Path.Combine(tempDir, "src", "DemoBook.ttf");
        Directory.CreateDirectory(Path.GetDirectoryName(srcFile)!);
        File.WriteAllBytes(srcFile, [0x00, 0x01, 0x00, 0x00]);
        try
        {
            var svc = new FontShelfService(Path.Combine(tempDir, "fonts"));
            string imported = svc.ImportFontFile(srcFile);
            Assert.Equal("DemoBook.ttf", imported);
            Assert.Contains("DemoBook.ttf", svc.GetCustomFontFiles());

            Assert.True(svc.DeleteCustomFont("DemoBook.ttf"));
            Assert.DoesNotContain("DemoBook.ttf", svc.GetCustomFontFiles());

            var sysFonts = svc.DiscoverSystemCjkFonts();
            Assert.NotEmpty(sysFonts);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
