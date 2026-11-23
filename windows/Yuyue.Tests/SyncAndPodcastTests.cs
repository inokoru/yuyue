using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;
using Yuyue.Audiobook;
using Yuyue.Core;
using Yuyue.Podcast;

namespace Yuyue.Tests;

public sealed class SyncAndPodcastTests
{
    [Fact]
    public void BuildWebDavFolderUrl_EscapesNestedCjkAndSpaceSegments()
    {
        string url = SyncService.BuildWebDavFolderUrl("https://dav.jianguoyun.com/dav/", "语阅 书库/子目录/sync");
        Assert.StartsWith("https://dav.jianguoyun.com/dav/", url);
        Assert.EndsWith("/sync/", url);
        Assert.DoesNotContain(" ", url);
        Assert.Contains("%E8%AF%AD%E9%98%85%20%E4%B9%A6%E5%BA%93", url);
        Assert.Contains("%E5%AD%90%E7%9B%AE%E5%BD%95", url);
    }

    [Fact]
    public void BuildWebDavFolderUrl_HandlesEmptyOrRootFolder()
    {
        Assert.Equal("https://example.com/dav/", SyncService.BuildWebDavFolderUrl("https://example.com/dav", ""));
        Assert.Equal("https://example.com/dav/", SyncService.BuildWebDavFolderUrl("https://example.com/dav/", " / \\ "));
    }

    [Fact]
    public void MergeJsonObjectKeys_PreservesLocalAndAddsMissingRemoteEntries()
    {
        string local = """{"q1":{"answer":"local-a"},"q2":{"answer":"local-b"}}""";
        string remote = """{"q1":{"answer":"remote-a"},"q3":{"answer":"remote-c"}}""";

        string? merged = SyncService.MergeJsonObjectKeys(local, remote, out bool added);
        Assert.True(added);
        Assert.NotNull(merged);

        using var doc = JsonDocument.Parse(merged!);
        Assert.Equal("local-a", doc.RootElement.GetProperty("q1").GetProperty("answer").GetString());
        Assert.Equal("local-b", doc.RootElement.GetProperty("q2").GetProperty("answer").GetString());
        Assert.Equal("remote-c", doc.RootElement.GetProperty("q3").GetProperty("answer").GetString());
    }

    [Fact]
    public void MergeJsonObjectKeys_ReturnsFalseWhenNoNewKeys()
    {
        string local = """{"a":1,"b":2}""";
        string remote = """{"a":99}""";

        SyncService.MergeJsonObjectKeys(local, remote, out bool added);
        Assert.False(added);
    }

    [Fact]
    public void MdnsAnnouncementPacket_ContainsPtrSrvTxtAndARecords()
    {
        byte[] pkt = RecordingShareServer.BuildMdnsAnnouncementPacket("Yuyue-PC", 53842, IPAddress.Parse("192.168.1.42"));
        Assert.True(pkt.Length > 40);
        // Header flags = 0x8400 (QR=1, AA=1), ANCOUNT = 4
        Assert.Equal(0x84, pkt[2]);
        Assert.Equal(0x00, pkt[3]);
        Assert.Equal(0x00, pkt[6]);
        Assert.Equal(0x04, pkt[7]);
        // Announcement (QR=1) must NOT trigger query responder (prevents mDNS reply loops)
        Assert.False(RecordingShareServer.ContainsMdnsServiceQuery(pkt, "_yuyue-rec"));
        string ascii = Encoding.ASCII.GetString(pkt);
        Assert.Contains("_yuyue-rec", ascii);
        Assert.Contains("Yuyue-PC", ascii);
        // Contains IPv4 bytes 192.168.1.42
        byte[] ip = [192, 168, 1, 42];
        Assert.True(pkt.AsSpan().IndexOf(ip) >= 0);

        // Simulated client mDNS query (QR=0) MUST be detected
        byte[] query = new byte[12 + "_yuyue-rec._tcp.local".Length];
        Encoding.ASCII.GetBytes("_yuyue-rec._tcp.local", query.AsSpan(12));
        Assert.True(RecordingShareServer.ContainsMdnsServiceQuery(query, "_yuyue-rec"));
    }

    [Fact]
    public void GroupWordsIntoMagazineSentences_SplitsByPunctuationAndPause()
    {
        HeardWord[] words =
        [
            new("Hello", 0.0, 0.4),
            new("world.", 0.4, 0.9),
            new("Next", 1.1, 1.4),
            new("sentence", 1.4, 1.9),
            new("after", 4.0, 4.4),
            new("pause", 4.4, 4.8),
        ];

        var sents = PodcastService.GroupWordsIntoMagazineSentences(words, "Host", "en-US");
        Assert.Equal(3, sents.Length);
        Assert.Equal("Hello world.", sents[0].TextOrig);
        Assert.Equal("Next sentence", sents[1].TextOrig);
        Assert.Equal("after pause", sents[2].TextOrig);
        Assert.Equal(0.0, sents[0].T0);
        Assert.Equal(4.0, sents[2].T0);
    }
}
