using System.Runtime.InteropServices.WindowsRuntime;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Yuyue.Core;

namespace Yuyue.Pdf;

/// <summary>
/// Extracts PDF metadata, character count, per-page reading text (via PdfPig),
/// and page-1 JPEG cover (via WinRT <see cref="PdfDocument"/>) for <see cref="YuyueCore.SetPdfInfo"/>
/// and PDF read-aloud.
/// </summary>
public static class PdfInfoReader
{
    public static async Task<PdfInfo> ReadAsync(string filePath)
    {
        var storageFile = await StorageFile.GetFileFromPathAsync(filePath);
        var winPdf = await PdfDocument.LoadFromFileAsync(storageFile);
        uint pageCount = winPdf.PageCount;
        if (pageCount == 0) throw new InvalidOperationException("PDF 文件没有页面");

        string? title = null;
        string[] authors = Array.Empty<string>();
        ulong totalChars = pageCount * 600UL;
        bool wordyFirstPage = false;

        try
        {
            await Task.Run(() =>
            {
                using var pig = UglyToad.PdfPig.PdfDocument.Open(filePath);
                if (!string.IsNullOrWhiteSpace(pig.Information.Title))
                    title = pig.Information.Title.Trim();
                if (!string.IsNullOrWhiteSpace(pig.Information.Author))
                    authors = [pig.Information.Author.Trim()];

                int sample = Math.Min(pig.NumberOfPages, 100);
                long chars = 0;
                for (int i = 1; i <= sample; i++)
                {
                    string pageText = ContentOrderTextExtractor.GetText(pig.GetPage(i));
                    int nonWs = pageText.Count(c => !char.IsWhiteSpace(c));
                    if (i == 1 && nonWs > 400) wordyFirstPage = true;
                    chars += nonWs;
                }
                totalChars = sample > 0 ? (ulong)(chars * pig.NumberOfPages / sample) : 0UL;
            });
        }
        catch { /* fallback to estimated characters if PdfPig cannot parse encrypted/exotic PDF */ }

        byte[]? cover = wordyFirstPage ? null : await RenderCoverJpegAsync(winPdf);
        return new PdfInfo(pageCount, title, authors, totalChars, cover);
    }

    public static async Task<byte[]?> RenderCoverJpegAsync(PdfDocument doc)
    {
        try
        {
            using var page = doc.GetPage(0);
            if (page.Size.Width <= 0 || page.Size.Height <= 0) return null;
            uint w = 600;
            uint h = (uint)Math.Round(page.Size.Height * 600.0 / page.Size.Width);
            if (h == 0 || h > 4000) return null;

            using var pngStream = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(pngStream, new PdfPageRenderOptions
            {
                DestinationWidth = w,
                DestinationHeight = h,
                BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255),
            });

            pngStream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(pngStream);
            using var softwareBitmap = await decoder.GetSoftwareBitmapAsync();

            using var jpgStream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, jpgStream);
            encoder.SetSoftwareBitmap(softwareBitmap);
            await encoder.FlushAsync();

            jpgStream.Seek(0);
            var buf = new byte[jpgStream.Size];
            await jpgStream.ReadAsync(buf.AsBuffer(), (uint)buf.Length, InputStreamOptions.None);
            return buf;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Extracts reading-order text from a 0-based PDF page index.
    /// </summary>
    public static string ExtractPageText(string filePath, int pageIndexZeroBased)
    {
        try
        {
            using var pig = UglyToad.PdfPig.PdfDocument.Open(filePath);
            int p = pageIndexZeroBased + 1;
            if (p < 1 || p > pig.NumberOfPages) return "";
            return ContentOrderTextExtractor.GetText(pig.GetPage(p)) ?? "";
        }
        catch
        {
            return "";
        }
    }

    public static IReadOnlyList<(string Title, int PageZeroBased, int Level)> ExtractOutline(string filePath)
    {
        var list = new List<(string, int, int)>();
        try
        {
            using var pig = UglyToad.PdfPig.PdfDocument.Open(filePath);
            if (pig.TryGetBookmarks(out var bookmarks))
            {
                void Walk(IEnumerable<UglyToad.PdfPig.Outline.BookmarkNode> nodes, int depth)
                {
                    foreach (var node in nodes)
                    {
                        if (node is UglyToad.PdfPig.Outline.DocumentBookmarkNode docNode && docNode.PageNumber >= 1)
                            list.Add((docNode.Title, docNode.PageNumber - 1, depth));
                        Walk(node.Children, depth + 1);
                    }
                }
                Walk(bookmarks.Roots, 0);
            }
        }
        catch { }
        return list;
    }

    public static IReadOnlyList<(int PageZeroBased, string Snippet)> SearchText(string filePath, string query)
    {
        var hits = new List<(int, string)>();
        if (string.IsNullOrWhiteSpace(query)) return hits;
        string q = query.Trim();
        try
        {
            using var pig = UglyToad.PdfPig.PdfDocument.Open(filePath);
            for (int i = 1; i <= pig.NumberOfPages && hits.Count < 80; i++)
            {
                string text = ContentOrderTextExtractor.GetText(pig.GetPage(i)) ?? "";
                int idx = text.IndexOf(q, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    int start = Math.Max(0, idx - 24);
                    int len = Math.Min(text.Length - start, q.Length + 56);
                    string snippet = text.Substring(start, len).Replace('\r', ' ').Replace('\n', ' ').Trim();
                    hits.Add((i - 1, snippet));
                }
            }
        }
        catch { }
        return hits;
    }
}
