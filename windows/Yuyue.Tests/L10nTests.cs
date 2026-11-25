using Xunit;
using Yuyue.Core;

namespace Yuyue.Tests;

[CollectionDefinition("L10n", DisableParallelization = true)]
public sealed class L10nCollection { }

[Collection("L10n")]
public sealed class L10nTests : IDisposable
{
    private readonly bool _prevEnglish;

    public L10nTests()
    {
        _prevEnglish = L10n.IsEnglish;
    }

    public void Dispose()
    {
        L10n.IsEnglish = _prevEnglish;
    }

    [Fact]
    public void Dictionary_LoadsMergedAppleAndroidWindowsEntries()
    {
        Assert.True(L10n.DictionaryCount >= 3500,
            $"Expected >= 3500 merged zh->en entries in Assets/L10n/en.json, got {L10n.DictionaryCount}");
    }

    [Fact]
    public void Translate_WhenChineseMode_ReturnsOriginalUnchanged()
    {
        L10n.IsEnglish = false;
        Assert.Equal("设置", L10n.T("设置"));
        Assert.Equal("其余 3 本", L10n.T("其余 3 本"));
        Assert.Equal("中文", L10n.Tr("中文", "English"));
    }

    [Theory]
    [InlineData("书架", "Library")]
    [InlineData("设置", "Settings")]
    [InlineData("通用", "General")]
    [InlineData("外观", "Appearance")]
    [InlineData("同步", "Sync")]
    [InlineData("存储", "Storage")]
    [InlineData("有声书强制对齐模型", "Audiobook Alignment Model")]
    [InlineData("广播剧音效素材库", "Audio Drama Sound Library")]
    [InlineData("点播放从这一页读起", "Press play to read from this page")]
    [InlineData("指着试听 · 点一下换", "Hover to preview · Click to switch")]
    [InlineData("下好即启用", "Activates when downloaded")]
    [InlineData("没有匹配的笔记", "No matching notes")]
    [InlineData("录音里没念这里，朗读补上", "Narrator skipped this passage — reading aloud with TTS")]
    [InlineData("重新录一段", "Re-record Sample")]
    public void Translate_StaticKeys_MatchExpectedEnglish(string zh, string expectedEn)
    {
        L10n.IsEnglish = true;
        Assert.Equal(expectedEn, L10n.T(zh));
    }

    [Theory]
    [InlineData("其余 3 本", "Other 3")]
    [InlineData("全部 12 本", "All 12")]
    [InlineData("1 本", "1 book")]
    [InlineData("5 本", "5 books")]
    [InlineData("划线 3", "Highlights 3")]
    [InlineData("想法 2", "Notes 2")]
    [InlineData("生词 7", "Words 7")]
    [InlineData("第 961 页 · 这一页", "Page 961 · This page")]
    [InlineData("第 1–2 / 14 页", "Pages 1–2 / 14")]
    [InlineData("第 4 / 14 页", "Page 4 / 14")]
    [InlineData("第 12 / 48 句", "Sentence 12 / 48")]
    [InlineData("本章还剩 15 分钟", "Chapter: 15 min left")]
    [InlineData("这一页还剩 2 分钟", "2 min left on this page")]
    [InlineData("还剩 26 小时 54 分", "26h 54m left")]
    [InlineData("还剩 3 小时", "3h left")]
    [InlineData("还剩 9 分", "9 min left")]
    [InlineData("本机可用 15.27 GB", "15.27 GB free")]
    [InlineData("书文件 12.2 MB", "Books 12.2 MB")]
    [InlineData("下载中 42%", "Downloading 42%")]
    [InlineData("3 分钟前同步", "Synced 3 min ago")]
    [InlineData("今天 14:20 同步", "Synced today at 14:20")]
    [InlineData("WebDAV · 待同步", "WebDAV · Pending sync")]
    [InlineData("3 台设备", "3 devices")]
    [InlineData("留下 8 条", "Kept 8")]
    [InlineData("正在导入 2 / 5…", "Importing 2 / 5…")]
    [InlineData("松开即可导入这 4 个文件", "Drop to import 4 files")]
    [InlineData("读了 12 天", "Read 12 days")]
    [InlineData("听了 3 小时 5 分", "Listened 3h 5m")]
    [InlineData("第 7 / 120 章", "Chapter 7 / 120")]
    [InlineData("取消下载 (65%)", "Cancel (65%)")]
    [InlineData("已下 4 / 12 集", "Downloaded 4 / 12")]
    [InlineData("第 3 集还在下，下好就接着放", "Downloading Ep. 3 — playback will resume automatically")]
    [InlineData("2026年11月25日", "Nov 25, 2026")]
    [InlineData("星期三", "Wednesday")]
    [InlineData("星期六", "Saturday")]
    [InlineData("12.2 MB · 占用你的个人云盘空间；播客单集体积很小，始终同步",
        "12.2 MB · Uses your cloud storage; podcast episodes are tiny and always sync")]
    public void Translate_DynamicTemplates_FormatProperly(string zh, string expectedEn)
    {
        L10n.IsEnglish = true;
        Assert.Equal(expectedEn, L10n.T(zh));
    }

    [Fact]
    public void Translate_MultiSegmentAndBulletWrappers_TranslateEachPart()
    {
        L10n.IsEnglish = true;
        Assert.Equal("· 26h 54m left", L10n.T("· 还剩 26 小时 54 分"));
        Assert.Equal("· Page 5", L10n.T("· 第 5 页"));
        Assert.Equal("Page 1 · Sentence 3 / 10", L10n.T("第 1 页 · 第 3 / 10 句"));
        Assert.Equal("45% · Chapter: 12 min left", L10n.T("45% · 本章还剩 12 分钟"));
    }

    [Fact]
    public void Translate_UnknownPureAscii_PassesThroughUntouched()
    {
        L10n.IsEnglish = true;
        Assert.Equal("EPUB / PDF / MOBI", L10n.T("EPUB / PDF / MOBI"));
        Assert.Equal("", L10n.T(null));
    }
}
