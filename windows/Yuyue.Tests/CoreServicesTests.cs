using System.IO.Compression;
using Xunit;
using Yuyue.Core;
using Yuyue.Speech;

namespace Yuyue.Tests;

public sealed class CoreServicesTests : IDisposable
{
    private readonly string _tempDir;

    public CoreServicesTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"yuyue-core-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void BuiltInEcdict_LooksUpExactAndInflectedEnglishWords()
    {
        var dict = new BuiltInEcdict();
        Assert.True(dict.IsAvailable, $"Built-in ECDICT missing at {dict.FilePath}");
        Assert.True(new FileInfo(dict.FilePath).Length > 1_000_000);

        var direct = dict.LookUp("book");
        Assert.NotNull(direct);
        Assert.Equal("book", direct!.Headword);
        Assert.False(string.IsNullOrWhiteSpace(direct.Meaning));

        // Punctuation stripping + inflected forms
        var punctuated = dict.LookUp("\"books,\"");
        Assert.NotNull(punctuated);
        Assert.False(string.IsNullOrWhiteSpace(punctuated!.Meaning));

        var inflected = dict.LookUp("walked");
        Assert.NotNull(inflected);
        Assert.Equal("walk", inflected!.Headword);

        var running = dict.LookUp("running");
        Assert.NotNull(running);
        Assert.False(string.IsNullOrWhiteSpace(running!.Meaning));
    }

    [Fact]
    public void SecretStore_DpapiRoundTripPersistsAndDeletesSecrets()
    {
        string dir = Path.Combine(_tempDir, "secrets");
        Directory.CreateDirectory(dir);

        var store1 = new SecretStore(dir);
        store1.Set("openai_key", "sk-test-secret-12345");
        store1.Set("webdav_pass", "p@ssw0rd!");
        Assert.True(File.Exists(Path.Combine(dir, "secrets.dpapi")));

        // Verify the file on disk is actually encrypted (does not contain plaintext secret)
        byte[] raw = File.ReadAllBytes(Path.Combine(dir, "secrets.dpapi"));
        string rawAscii = System.Text.Encoding.UTF8.GetString(raw);
        Assert.DoesNotContain("sk-test-secret-12345", rawAscii);

        // Fresh SecretStore instance must decrypt both keys identically
        var store2 = new SecretStore(dir);
        Assert.Equal("sk-test-secret-12345", store2.Get("openai_key"));
        Assert.Equal("p@ssw0rd!", store2.Get("webdav_pass"));

        // Setting empty deletes the key
        store2.Set("openai_key", "");
        var store3 = new SecretStore(dir);
        Assert.Equal("", store3.Get("openai_key"));
        Assert.Equal("p@ssw0rd!", store3.Get("webdav_pass"));
    }

    [Fact]
    public void NoteExportHelpers_HashNotionIdAndObsidianFilename()
    {
        var m1 = new Mark("id-1", 0, new TextPoint(0, 0), new TextPoint(0, 5), "hello", MarkStyle.Yellow, "note-a", 1, false);
        var m2 = new Mark("id-2", 0, new TextPoint(0, 0), new TextPoint(0, 5), "hello", MarkStyle.Pink, "note-a", 2, true);
        var m3 = new Mark("id-3", 0, new TextPoint(0, 0), new TextPoint(0, 5), "hello", MarkStyle.Yellow, "note-b", 3, false);

        // Same text + note produces same FNV-1a 31-bit hash regardless of style/id; different note changes hash
        int h1 = NoteExportService.ComputeMarkHash(m1);
        int h2 = NoteExportService.ComputeMarkHash(m2);
        int h3 = NoteExportService.ComputeMarkHash(m3);
        Assert.True(h1 >= 0);
        Assert.Equal(h1, h2);
        Assert.NotEqual(h1, h3);

        // Notion URL & dashed UUID extraction
        Assert.Equal(
            "0123456789abcdef0123456789abcdef",
            NoteExportService.ExtractNotionPageId("https://www.notion.so/My-Notes-01234567-89ab-cdef-0123-456789abcdef?pvs=4"));
        Assert.Null(NoteExportService.ExtractNotionPageId("not-a-notion-id"));

        // Obsidian filename sanitization
        Assert.Equal("A-B-C", NoteExportService.SanitizeObsidianFileName("A/B:C"));
        Assert.Equal("未命名书籍", NoteExportService.SanitizeObsidianFileName("   "));
    }

    [Fact]
    public async Task RustCore_ImportMarkAndSoundLibraryZip_SmokeTest()
    {
        string libDir = Path.Combine(_tempDir, "rust-lib");
        Directory.CreateDirectory(libDir);
        string epub = TestPaths.Fixture("books", "beiying.epub");
        Assert.True(File.Exists(epub));

        using var core = YuyueCore.Open(libDir, "yuyue-tests", "windows");
        var imported = core.ImportBook(epub);
        Assert.False(string.IsNullOrWhiteSpace(imported.Id));
        Assert.Equal(BookFormat.Epub, imported.Format);

        var mark = core.AddMark(
            imported.Id,
            0,
            new TextPoint(0, 0),
            new TextPoint(0, 6),
            "我与父亲不相见",
            MarkStyle.Yellow,
            "经典开篇");
        Assert.False(string.IsNullOrWhiteSpace(mark.Id));

        var counts = core.SnapshotCounts();
        Assert.True(counts.Books >= 1);
        Assert.True(counts.Notes >= 1);

        // SoundLibraryService: importing a zip without library.json throws InvalidDataException
        string badZip = Path.Combine(_tempDir, "bad-sounds.zip");
        using (var archive = ZipFile.Open(badZip, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("readme.txt");
            using var sw = new StreamWriter(entry.Open());
            await sw.WriteAsync("not a sound library");
        }
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SoundLibraryService.ImportLocalZipAsync(core, libDir, badZip));
        Assert.False(SoundLibraryService.IsInstalled(libDir));
    }
}
