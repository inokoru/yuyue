using Xunit;
using Yuyue.Core;
using Yuyue.Pdf;

namespace Yuyue.Tests;

public sealed class PdfInfoReaderTests
{
    private static string SamplePdf => TestPaths.Fixture("books", "sample.pdf");

    [Fact]
    public void ExtractPageLayout_SamplePdf_ProducesCleanCjkTextAndNormalizedGlyphs()
    {
        Assert.True(File.Exists(SamplePdf), $"Fixture missing: {SamplePdf}");

        var layout = PdfInfoReader.ExtractPageLayout(SamplePdf, 0);
        Assert.Equal(0, layout.PageIndex);
        Assert.False(string.IsNullOrWhiteSpace(layout.Text));
        Assert.NotEmpty(layout.Glyphs);
        Assert.Contains("背影", layout.Text);

        foreach (var g in layout.Glyphs)
        {
            Assert.InRange(g.X, 0.0, 1.0);
            Assert.InRange(g.Y, 0.0, 1.0);
            Assert.True(g.W > 0 && g.X + g.W <= 1.0001);
            Assert.True(g.H > 0 && g.Y + g.H <= 1.0001);
            Assert.True(g.CharStart >= 0 && g.CharEnd <= layout.Text.Length && g.CharEnd > g.CharStart);
        }
    }

    [Fact]
    public void MergeLineRects_And_HitTestGlyph_AlignWithCorePageSentences()
    {
        var layout = PdfInfoReader.ExtractPageLayout(SamplePdf, 0);
        var sentences = YuyueCoreMethods.PageSentences(layout.Text);
        Assert.NotEmpty(sentences);

        // Find the first multi-character sentence and verify exact text lookup + line-rect merging + hit testing
        var sent = sentences.First(s => s.Text.Length >= 6);
        int idx = layout.Text.IndexOf(sent.Text, StringComparison.Ordinal);
        Assert.True(idx >= 0, $"Sentence '{sent.Text}' not found in extracted page layout text.");

        var rects = PdfInfoReader.MergeLineRects(layout, idx, idx + sent.Text.Length);
        Assert.NotEmpty(rects);
        foreach (var r in rects)
        {
            Assert.InRange(r.X, 0.0, 1.0);
            Assert.InRange(r.Y, 0.0, 1.0);
            Assert.True(r.W > 0.01);
            Assert.True(r.H > 0.004);
        }

        // Hit-testing the center of the first merged rect must resolve to a glyph inside this sentence
        var first = rects[0];
        int hit = PdfInfoReader.HitTestGlyph(layout, first.X + first.W * 0.5, first.Y + first.H * 0.5);
        Assert.True(hit >= 0 && hit < layout.Glyphs.Length);
        Assert.True(layout.Glyphs[hit].CharStart >= idx - 1 && layout.Glyphs[hit].CharEnd <= idx + sent.Text.Length + 1);
    }

    [Fact]
    public void SearchText_FindsKnownPhraseOnPageZero()
    {
        var hits = PdfInfoReader.SearchText(SamplePdf, "背影");
        Assert.NotEmpty(hits);
        Assert.Contains(hits, h => h.PageZeroBased == 0 && h.Snippet.Contains("背影"));
    }
}
