using System.Net.Sockets;
using System.Text;
using Xunit;
using Yuyue.Core;
using Yuyue.Speech;

namespace Yuyue.Tests;

public sealed class ReleaseReadinessTests : IDisposable
{
    private readonly string _tempDir;

    public ReleaseReadinessTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"yuyue-reltest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void SenseVoiceCore_FrontEndAndCtcWords_MatchCrossPlatformSpec()
    {
        var meta = new Dictionary<string, string>
        {
            ["lfr_window_size"] = "7",
            ["lfr_window_shift"] = "6",
            ["neg_mean"] = string.Join(",", Enumerable.Repeat("0", 560)),
            ["inv_stddev"] = string.Join(",", Enumerable.Repeat("1", 560)),
            ["lang_zh"] = "3",
            ["lang_auto"] = "0",
            ["with_itn"] = "14",
            ["without_itn"] = "15",
        };
        string tokens = "<blk> 0\n<|zh|> 1\n<|NEUTRAL|> 2\n<|Speech|> 3\n<|withitn|> 4\n你 5\n好 6\n， 7\n▁hel 8\nlo 9\n▁world 10\n。 11\n世界 12";
        using var sv = new SenseVoice(meta, tokens);

        Assert.Equal(3, sv.LanguageId("zh-Hans"));
        Assert.Equal(0, sv.LanguageId("fr"));
        Assert.Equal(14, sv.TextNormId(true));
        Assert.Equal(15, sv.TextNormId(false));

        float[] sec = Enumerable.Range(0, 16_000).Select(i => MathF.Sin(i * 0.05f) * 0.3f).ToArray();
        var asrIn = sv.Input(sec, 16_000);
        Assert.Equal(17u, asrIn.Frames);
        Assert.Equal(560u, asrIn.Dim);
        Assert.Equal(17 * 560, asrIn.Data.Length);

        var words = sv.Words([1, 2, 3, 4, 5, 5, 0, 6, 7, 0, 8, 9, 10, 11, 12], 10.0);
        Assert.Equal(["你", "好，", "hello", "world。", "世", "界"], words.Select(w => w.Text).ToArray());
    }

    [Fact]
    public void SenseVoiceListener_FindQuietestOffset_PicksSilentGapInTailThird()
    {
        int rate = 16_000;
        float[] audio = new float[rate * 3]; // 3.0s
        for (int i = 0; i < audio.Length; i++) audio[i] = 0.25f;
        // Inject 0.2s of silence at 2.4s..2.6s (inside the tail third >= 2.0s)
        for (int i = (int)(2.4 * rate); i < (int)(2.6 * rate); i++) audio[i] = 0.0f;

        int cut = SenseVoiceListener.FindQuietestOffset(audio, audio.Length * 2 / 3, rate);
        Assert.InRange(cut, (int)(2.35 * rate), (int)(2.65 * rate));
    }

    [Fact]
    public async Task WifiTransfer_MalformedHttpRequests_Return400WithoutCrashing()
    {
        Assert.False(WifiTransferService.TryParseQuery("?id=%ZZ&offset=0", out _));
        Assert.True(WifiTransferService.TryParseQuery("?id=abc_1&offset=4096", out var q));
        Assert.Equal("abc_1", q["id"]);
        Assert.Equal("4096", q["offset"]);

        using var lib = new LibraryService(_tempDir);
        using var wifi = new WifiTransferService(lib);
        string url = wifi.Start();
        var uri = new Uri(url);

        static async Task<string> SendRawAsync(int port, string rawReq)
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync("127.0.0.1", port);
            await using var s = tcp.GetStream();
            byte[] reqBytes = Encoding.ASCII.GetBytes(rawReq);
            await s.WriteAsync(reqBytes);
            byte[] buf = new byte[512];
            int n = await s.ReadAsync(buf);
            return Encoding.ASCII.GetString(buf, 0, n);
        }

        string badLenResp = await SendRawAsync(uri.Port, "POST /queue HTTP/1.1\r\nHost: localhost\r\nContent-Length: -12\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 400 Bad Request", badLenResp);

        string nanLenResp = await SendRawAsync(uri.Port, "POST /queue HTTP/1.1\r\nHost: localhost\r\nContent-Length: notanumber\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 400 Bad Request", nanLenResp);

        string badOffsetResp = await SendRawAsync(uri.Port, "PUT /chunk?id=test1&offset=-1 HTTP/1.1\r\nHost: localhost\r\nContent-Length: 0\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 400 Bad Request", badOffsetResp);
    }

    [Fact]
    public void UpdateManifestAndDeepLinks_ParseExpectedReleaseFormats()
    {
        string updateJson = """
        {
          "version": "0.1.3",
          "code": 5,
          "windows": {
            "x64": {
              "url": "https://github.com/inokoru/yuyue/releases/download/v0.1.3/Yuyue-0.1.3-windows-x64-Setup.exe",
              "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
            }
          }
        }
        """;
        var (url, sha) = UpdateAndFeedbackService.ParseWinInstallerJson(updateJson, "x64");
        Assert.Equal("https://github.com/inokoru/yuyue/releases/download/v0.1.3/Yuyue-0.1.3-windows-x64-Setup.exe", url);
        Assert.Equal("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", sha);

        string sums = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef  Yuyue-0.1.3-windows-x64-Setup.exe\n";
        Assert.Equal("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            UpdateAndFeedbackService.ParseSha256Sums(sums, "Yuyue-0.1.3-windows-x64-Setup.exe"));

        Assert.Equal(("open", "book-123"), App.ParseDeepLinkUri("yuyue://open/book-123"));
        Assert.Equal(("settings", "storage"), App.ParseDeepLinkUri("yuyue://settings/storage"));
        Assert.Equal(("tab", "podcasts"), App.ParseDeepLinkUri("yuyue://tab/podcasts"));
    }

    [Fact]
    public void WindowDpiMath_RoundTripsWithoutBallooningAcrossHiDpiScales()
    {
        // 150% DPI (144): physical 1620x1110 must convert back to 1080x740 DIPs, not stay 1620x1110!
        Assert.Equal((1080, 740), Yuyue.Design.ThemeHelper.PhysicalSizeToDip(1620, 1110, 144));
        // 200% DPI (192): physical 2080x1440 -> 1040x720 DIPs
        Assert.Equal((1040, 720), Yuyue.Design.ThemeHelper.PhysicalSizeToDip(2080, 1440, 192));
        // 100% DPI (96): unchanged
        Assert.Equal((1020, 640), Yuyue.Design.ThemeHelper.PhysicalSizeToDip(1020, 640, 96));
    }

    [Fact]
    public void PaperAppearance_PerBookOverride_DoesNotMutateGlobalPrefs()
    {
        using var lib = new LibraryService(_tempDir);
        lib.Prefs.PaperFollowAppearance = true;
        lib.Prefs.PaperLightId = "kami";
        lib.Prefs.PaperDarkId = "kaminight";

        var baseSettings = lib.GetReaderSettings();
        bool changed = PaperAppearance.PickPreset(lib, Paper.Dark, baseSettings, out var updated, appDarkOverride: false, perBookOverride: true);

        Assert.True(changed);
        Assert.Equal(Paper.Dark, updated.Paper);
        Assert.Null(updated.CustomPaper);
        // Global appearance prefs must remain completely untouched!
        Assert.True(lib.Prefs.PaperFollowAppearance);
        Assert.Equal("kami", lib.Prefs.PaperLightId);
        Assert.Equal("kaminight", lib.Prefs.PaperDarkId);
    }

    [Fact]
    public void ShelfAndKeptBooks_PartitionsPaperRecordsFromActiveShelf()
    {
        using var lib = new LibraryService(_tempDir);
        var paper = lib.Core.AddRecordBook("纸上山河", ["林徽因"], ReadingStatus.Reading);
        Assert.True(paper.Record);

        var (shelf, kept) = lib.GetShelfAndKeptBooks();
        Assert.DoesNotContain(shelf, b => b.Id == paper.Id);
        Assert.Contains(kept, b => b.Id == paper.Id);
    }
}
