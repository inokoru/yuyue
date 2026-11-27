using Xunit;
using Yuyue.Core;
using Yuyue.Design;
using Yuyue.UI.Settings;

namespace Yuyue.Tests;

public sealed class Win11RebuildTests : IDisposable
{
    private readonly string _tempDir;

    public Win11RebuildTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"yuyue-win11-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void Win11DevicePrefs_DefaultsAndPersistence_WorkCleanly()
    {
        using (var lib = new LibraryService(_tempDir))
        {
            // Locked Windows 11 native defaults
            Assert.True(lib.Prefs.OpenBookInTab);
            Assert.True(lib.Prefs.RestoreLastTabs);
            Assert.Equal("tray_when_playing", lib.Prefs.CloseWindowBehavior);
            Assert.False(lib.Prefs.ShelfSingleClickOpen); // default: single-click select + double-click open
            Assert.Equal("grid", lib.Prefs.ShelfViewMode);
            Assert.Equal("all", lib.Prefs.LastShelfSection);
            Assert.Equal("smart", lib.Prefs.ReaderSpaceAction); // default: smart space
            Assert.True(lib.Prefs.UseWarmMica);

            // Mutate & persist including SavedTabEntry with SplitBookId
            lib.Prefs.OpenBookInTab = false;
            lib.Prefs.CloseWindowBehavior = "always_tray";
            lib.Prefs.ShelfSingleClickOpen = true;
            lib.Prefs.ShelfViewMode = "list";
            lib.Prefs.LastShelfSection = "podcasts";
            lib.Prefs.ReaderSpaceAction = "listen";
            lib.Prefs.UseWarmMica = false;
            lib.Prefs.NavPaneCompact = true;
            lib.Prefs.LastOpenTabs =
            [
                new SavedTabEntry("book-a", "reader", "book-b"),
                new SavedTabEntry("book-pdf", "pdf"),
            ];
            lib.SavePrefs();
        }

        using var reloaded = new LibraryService(_tempDir);
        Assert.False(reloaded.Prefs.OpenBookInTab);
        Assert.Equal("always_tray", reloaded.Prefs.CloseWindowBehavior);
        Assert.True(reloaded.Prefs.ShelfSingleClickOpen);
        Assert.Equal("list", reloaded.Prefs.ShelfViewMode);
        Assert.Equal("podcasts", reloaded.Prefs.LastShelfSection);
        Assert.Equal("listen", reloaded.Prefs.ReaderSpaceAction);
        Assert.False(reloaded.Prefs.UseWarmMica);
        Assert.True(reloaded.Prefs.NavPaneCompact);
        Assert.Equal(2, reloaded.Prefs.LastOpenTabs.Count);
        Assert.Equal("book-a", reloaded.Prefs.LastOpenTabs[0].BookId);
        Assert.Equal("book-b", reloaded.Prefs.LastOpenTabs[0].SplitBookId);
        Assert.Equal("pdf", reloaded.Prefs.LastOpenTabs[1].Kind);
    }

    [Fact]
    public void ThemeHelper_ResolvesAll4OrientalTintsPlusWindowsSystemBlue()
    {
        Assert.Equal(5, ThemeHelper.AllTints.Count);
        Assert.Equal("camel", ThemeHelper.ResolveTint("camel").Id);
        Assert.Equal("sage", ThemeHelper.ResolveTint("sage").Id);
        Assert.Equal("mist", ThemeHelper.ResolveTint("mist").Id);
        Assert.Equal("blush", ThemeHelper.ResolveTint("blush").Id);
        Assert.Equal("winblue", ThemeHelper.ResolveTint("winblue").Id);
        Assert.Equal("camel", ThemeHelper.ResolveTint("unknown-id").Id);

        using var lib = new LibraryService(_tempDir);
        lib.Prefs.TintId = "winblue";
        Assert.Equal("winblue", lib.Prefs.Tint.Id);
    }

    [Fact]
    public void SettingsSearchIndex_MatchesChineseAndEnglishQueriesAcrossDomains()
    {
        Assert.Contains(SettingsSearchIndex.Search("托盘"), e => e.PageId == "general");
        Assert.Contains(SettingsSearchIndex.Search("mica"), e => e.PageId == "appearance");
        Assert.Contains(SettingsSearchIndex.Search("系统蓝"), e => e.PageId == "appearance");
        Assert.Contains(SettingsSearchIndex.Search("空格"), e => e.PageId == "general");
        Assert.Contains(SettingsSearchIndex.Search("webdav"), e => e.PageId == "sync");
        Assert.Contains(SettingsSearchIndex.Search("obsidian"), e => e.PageId == "export");
        Assert.Contains(SettingsSearchIndex.Search("kokoro"), e => e.PageId == "listening");
        Assert.Contains(SettingsSearchIndex.Search("deepseek"), e => e.PageId == "companion");
        Assert.Empty(SettingsSearchIndex.Search("   "));
    }
}
