using System.Collections.Concurrent;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Yuyue.Core;

namespace Yuyue.Pdf;

/// <summary>Normalized (0..1) top-down glyph rectangle on a PDF page, mapped to UTF-16 offsets in <see cref="PdfPageLayout.Text"/>.</summary>
public sealed record PdfGlyphRect(int CharStart, int CharEnd, double X, double Y, double W, double H);

/// <summary>Normalized (0..1) top-down line bounding box produced by merging adjacent glyphs on the same line.</summary>
public sealed record PdfLineRect(double X, double Y, double W, double H);

/// <summary>Reading-order extracted text and exact normalized glyph geometry for a single 0-based PDF page.</summary>
public sealed record PdfPageLayout(int PageIndex, string Text, PdfGlyphRect[] Glyphs);

/// <summary>
/// Extracts PDF metadata, character count, per-page reading text (via PdfPig),
/// and page-1 JPEG cover (via WinRT <see cref="PdfDocument"/>) for <see cref="YuyueCore.SetPdfInfo"/>
/// and PDF read-aloud.
/// </summary>
public static class PdfInfoReader
{
    private static readonly string[] JunkTitlePrefixes =
    [
        "microsoft word", "microsoft powerpoint", "microsoft excel",
        "untitled", "document", "slide ", "presentation", "wps",
        "acrobat", "pdf document", "scanned", "image", "print",
    ];

    private static readonly HashSet<string> JunkAuthors = new(StringComparer.OrdinalIgnoreCase)
    {
        "administrator", "admin", "user", "owner", "pc", "windows", "author",
        "unknown", "lenovo", "dell", "hp", "asus", "acer", "microsoft", "adobe", "wps",
    };

    internal static string? SanitizePdfTitle(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string t = raw.Trim();
        if (t.Length < 2 || t.Length > 180) return null;
        string lower = t.ToLowerInvariant();
        if (lower.EndsWith(".pdf") || lower.EndsWith(".doc") || lower.EndsWith(".docx")
            || lower.EndsWith(".ppt") || lower.EndsWith(".pptx") || lower.EndsWith(".wps")
            || lower.EndsWith(".tex") || lower.EndsWith(".indd"))
            return null;
        if (JunkTitlePrefixes.Any(p => lower.StartsWith(p, StringComparison.Ordinal)))
            return null;
        if (t.All(c => char.IsDigit(c) || c is '-' or '_' or '.' or ' '))
            return null;
        return t;
    }

    internal static string? SanitizePdfAuthor(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string a = raw.Trim();
        if (a.Length < 2 || a.Length > 80) return null;
        if (JunkAuthors.Contains(a)) return null;
        if (a.Contains(":\\") || a.StartsWith("/Users/", StringComparison.OrdinalIgnoreCase)) return null;
        return a;
    }

    public static string DetectLanguage(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "zh-Hans";
        int cjk = 0, latin = 0;
        foreach (char c in text)
        {
            if (c >= 0x4E00 && c <= 0x9FFF) cjk++;
            else if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')) latin++;
        }
        return latin > cjk * 2 ? "en-US" : "zh-Hans";
    }

    private static readonly ConcurrentDictionary<string, double[]> PageAspectsCache = new(StringComparer.OrdinalIgnoreCase);

    private static bool IsCjkOrFullWidth(char ch) =>
        (ch >= '\u2E80' && ch <= '\u9FFF') ||
        (ch >= '\uF900' && ch <= '\uFAFF') ||
        (ch >= '\u3000' && ch <= '\u303F') ||
        (ch >= '\uFF00' && ch <= '\uFFEF') ||
        ch is '“' or '”' or '‘' or '’' or '—' or '…';

    /// <summary>
    /// Fast lightweight read of per-page height/width aspect ratios from an already-loaded <see cref="PdfDocument"/>
    /// (reads page box metadata only, zero bitmap rendering).
    /// </summary>
    public static double[] GetPageAspectRatios(PdfDocument winPdf, string? cacheKeyPath = null)
    {
        int count = (int)winPdf.PageCount;
        if (count <= 0) return [];
        if (!string.IsNullOrWhiteSpace(cacheKeyPath) &&
            PageAspectsCache.TryGetValue(cacheKeyPath, out var cached) &&
            cached.Length == count)
        {
            return cached;
        }

        var ratios = new double[count];
        for (int i = 0; i < count; i++)
        {
            double ratio = 1.414;
            try
            {
                using var p = winPdf.GetPage((uint)i);
                if (p.Size.Width > 0 && p.Size.Height > 0)
                    ratio = Math.Clamp(p.Size.Height / p.Size.Width, 0.25, 4.0);
            }
            catch { }
            ratios[i] = ratio;
        }

        if (!string.IsNullOrWhiteSpace(cacheKeyPath))
            PageAspectsCache[cacheKeyPath] = ratios;
        return ratios;
    }

    /// <summary>
    /// Lightweight per-page height/width aspect ratio reader by file path (uses cached values when available).
    /// </summary>
    public static double[] ReadPageAspectRatios(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return [];
        if (PageAspectsCache.TryGetValue(filePath, out var cached)) return cached;
        try
        {
            using var pig = UglyToad.PdfPig.PdfDocument.Open(filePath);
            int n = pig.NumberOfPages;
            var ratios = new double[n];
            for (int i = 1; i <= n; i++)
            {
                var p = pig.GetPage(i);
                ratios[i - 1] = p.Width > 0 && p.Height > 0
                    ? Math.Clamp(p.Height / p.Width, 0.25, 4.0)
                    : 1.414;
            }
            PageAspectsCache[filePath] = ratios;
            return ratios;
        }
        catch
        {
            return [];
        }
    }

    public static async Task<PdfInfo> ReadAsync(string filePath)
    {
        var storageFile = await StorageFile.GetFileFromPathAsync(filePath);
        PdfDocument winPdf;
        try
        {
            winPdf = await PdfDocument.LoadFromFileAsync(storageFile);
        }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x80070056) || ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("这份 PDF 受密码保护，语阅无法直接打开。请先解除密码后再导入。", ex);
        }

        if (winPdf.IsPasswordProtected)
            throw new InvalidOperationException("这份 PDF 受密码保护，语阅无法直接打开。请先解除密码后再导入。");

        uint pageCount = winPdf.PageCount;
        if (pageCount == 0) throw new InvalidOperationException("PDF 文件没有页面");
        _ = GetPageAspectRatios(winPdf, filePath);

        string? title = null;
        string[] authors = Array.Empty<string>();
        ulong totalChars = pageCount * 600UL;
        bool wordyFirstPage = false;

        try
        {
            await Task.Run(() =>
            {
                using var pig = UglyToad.PdfPig.PdfDocument.Open(filePath);
                title = SanitizePdfTitle(pig.Information.Title);
                if (SanitizePdfAuthor(pig.Information.Author) is string cleanAuthor)
                    authors = [cleanAuthor];

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
    /// Extracts reading-order text AND exact normalized top-down glyph geometry for a 0-based PDF page,
    /// so <see cref="YuyueCoreMethods.PageSentences"/>, drag selection, and persistent marks share identical UTF-16 offsets.
    /// </summary>
    public static PdfPageLayout ExtractPageLayout(string filePath, int pageIndexZeroBased)
    {
        try
        {
            using var pig = UglyToad.PdfPig.PdfDocument.Open(filePath);
            int p = pageIndexZeroBased + 1;
            if (p < 1 || p > pig.NumberOfPages)
                return new PdfPageLayout(pageIndexZeroBased, "", []);

            return ExtractPageLayoutCore(pig.GetPage(p), pageIndexZeroBased, includeGlyphs: true);
        }
        catch
        {
            return new PdfPageLayout(pageIndexZeroBased, "", []);
        }
    }

    private static PdfPageLayout ExtractPageLayoutCore(UglyToad.PdfPig.Content.Page page, int pageIndexZeroBased, bool includeGlyphs)
    {
        try
        {
            double pw = page.Width > 0 ? page.Width : 612.0;
            double ph = page.Height > 0 ? page.Height : 792.0;

            var sb = new StringBuilder();
            var glyphs = includeGlyphs ? new List<PdfGlyphRect>() : null;
            bool hasLetters = false;
            double prevEndX = double.NaN;
            double prevBaseY = double.NaN;
            double prevHeight = 10.0;

            foreach (var letter in page.Letters)
            {
                string val = letter.Value;
                if (string.IsNullOrEmpty(val)) continue;
                hasLetters = true;

                var rect = letter.GlyphRectangle;
                double baseY = letter.StartBaseLine.Y;
                double startX = Math.Min(rect.Left, letter.StartBaseLine.X);
                double endX = Math.Max(rect.Right, letter.EndBaseLine.X);
                double h = Math.Max(rect.Height, letter.PointSize > 0 ? letter.PointSize : 10.0);
                double w = Math.Max(endX - startX, h * 0.25);
                double top = Math.Max(rect.Top, baseY + h * 0.82);

                if (!double.IsNaN(prevBaseY))
                {
                    double dy = Math.Abs(baseY - prevBaseY);
                    double lineTol = Math.Max(prevHeight, h) * 0.55;
                    if (dy > lineTol)
                    {
                        if (sb.Length > 0 && sb[^1] != '\n')
                            sb.Append('\n');
                    }
                    else
                    {
                        double dx = startX - prevEndX;
                        bool cjk = sb.Length > 0 && (IsCjkOrFullWidth(sb[^1]) || IsCjkOrFullWidth(val[0]));
                        double spaceTol = Math.Max(prevHeight, h) * (cjk ? 1.15 : 0.22);
                        if (dx > spaceTol && sb.Length > 0 && !char.IsWhiteSpace(sb[^1]) && !char.IsWhiteSpace(val[0]))
                            sb.Append(' ');
                    }
                }

                int charStart = sb.Length;
                sb.Append(val);
                int charEnd = sb.Length;

                if (glyphs is not null)
                {
                    double nx = Math.Clamp(startX / pw, 0.0, 1.0);
                    double ny = Math.Clamp((ph - top) / ph, 0.0, 1.0);
                    double nw = Math.Clamp(w / pw, 0.001, 1.0 - nx);
                    double nh = Math.Clamp(h / ph, 0.004, 1.0 - ny);
                    glyphs.Add(new PdfGlyphRect(charStart, charEnd, nx, ny, nw, nh));
                }

                prevEndX = endX;
                prevBaseY = baseY;
                prevHeight = h;
            }

            if (hasLetters)
                return new PdfPageLayout(pageIndexZeroBased, sb.ToString(), glyphs?.ToArray() ?? []);

            string fallback = ContentOrderTextExtractor.GetText(page) ?? "";
            return new PdfPageLayout(pageIndexZeroBased, fallback, []);
        }
        catch
        {
            return new PdfPageLayout(pageIndexZeroBased, "", []);
        }
    }

    /// <summary>
    /// Merges adjacent glyph rects within UTF-16 range [<paramref name="startChar"/>, <paramref name="endChar"/>)
    /// into clean line-level normalized rectangles for highlights, underlines, and speech tracking.
    /// </summary>
    public static List<PdfLineRect> MergeLineRects(PdfPageLayout layout, int startChar, int endChar)
    {
        var result = new List<PdfLineRect>();
        if (layout.Glyphs.Length == 0 || endChar <= startChar) return result;

        double curX0 = 0, curY0 = 0, curX1 = 0, curY1 = 0;
        bool hasCur = false;

        foreach (var g in layout.Glyphs)
        {
            if (g.CharEnd <= startChar) continue;
            if (g.CharStart >= endChar) break;

            double gx0 = g.X, gy0 = g.Y, gx1 = g.X + g.W, gy1 = g.Y + g.H;
            if (!hasCur)
            {
                curX0 = gx0; curY0 = gy0; curX1 = gx1; curY1 = gy1;
                hasCur = true;
                continue;
            }

            double curH = Math.Max(curY1 - curY0, 0.006);
            double midCur = (curY0 + curY1) * 0.5;
            double midNew = (gy0 + gy1) * 0.5;
            bool sameLine = Math.Abs(midNew - midCur) <= Math.Max(curH, g.H) * 0.75
                            && gx0 >= curX0 - 0.01
                            && gx0 - curX1 <= Math.Max(curH * 3.5, 0.08);
            if (sameLine)
            {
                curX0 = Math.Min(curX0, gx0);
                curY0 = Math.Min(curY0, gy0);
                curX1 = Math.Max(curX1, gx1);
                curY1 = Math.Max(curY1, gy1);
            }
            else
            {
                result.Add(new PdfLineRect(curX0, curY0, Math.Max(0.002, curX1 - curX0), Math.Max(0.005, curY1 - curY0)));
                curX0 = gx0; curY0 = gy0; curX1 = gx1; curY1 = gy1;
            }
        }

        if (hasCur)
            result.Add(new PdfLineRect(curX0, curY0, Math.Max(0.002, curX1 - curX0), Math.Max(0.005, curY1 - curY0)));

        return result;
    }

    /// <summary>Hit-tests a normalized (0..1) page point against extracted glyph boxes, returning its glyph index or -1.</summary>
    public static int HitTestGlyph(PdfPageLayout layout, double normX, double normY)
    {
        if (layout.Glyphs.Length == 0) return -1;

        int bestIdx = -1;
        double bestScore = double.MaxValue;

        for (int i = 0; i < layout.Glyphs.Length; i++)
        {
            var g = layout.Glyphs[i];
            if (normX >= g.X - 0.004 && normX <= g.X + g.W + 0.004 &&
                normY >= g.Y - 0.006 && normY <= g.Y + g.H + 0.006)
            {
                return i;
            }

            double cx = g.X + g.W * 0.5;
            double cy = g.Y + g.H * 0.5;
            double dy = Math.Abs(normY - cy);
            double dx = Math.Abs(normX - cx);
            // Weight vertical distance strongly so dragging selects cleanly line-by-line
            double score = dy * 4.5 + dx;
            if (score < bestScore)
            {
                bestScore = score;
                bestIdx = i;
            }
        }

        return bestScore < 0.25 ? bestIdx : -1;
    }

    /// <summary>
    /// Extracts reading-order text from a 0-based PDF page index.
    /// </summary>
    public static string ExtractPageText(string filePath, int pageIndexZeroBased) =>
        ExtractPageLayout(filePath, pageIndexZeroBased).Text;

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

    /// <summary>
    /// Normalizes whitespace and line-breaks consistently between PDF search and overlay rendering
    /// (collapsing line breaks between CJK characters without spaces, and collapsing Latin line breaks to a single space),
    /// returning exact UTF-16 <c>(Start, Length)</c> ranges in <paramref name="pageText"/>.
    /// </summary>
    public static List<(int Start, int Length)> FindNormalizedMatches(string pageText, string query)
    {
        var results = new List<(int Start, int Length)>();
        if (string.IsNullOrEmpty(pageText) || string.IsNullOrWhiteSpace(query))
            return results;

        static (string Norm, List<int> Starts, List<int> Ends) BuildNormalizedMap(string src)
        {
            var sb = new StringBuilder(src.Length);
            var starts = new List<int>(src.Length);
            var ends = new List<int>(src.Length);
            int i = 0;
            while (i < src.Length)
            {
                char ch = src[i];
                if (char.IsWhiteSpace(ch))
                {
                    int wsStart = i;
                    while (i < src.Length && char.IsWhiteSpace(src[i])) i++;
                    if (sb.Length == 0 || i >= src.Length) continue;
                    char prev = sb[^1];
                    char next = src[i];
                    // Omit whitespace across line breaks between CJK/full-width characters; collapse other runs to ' '
                    bool omitSpace = IsCjkOrFullWidth(prev) && IsCjkOrFullWidth(next);
                    if (!omitSpace)
                    {
                        sb.Append(' ');
                        starts.Add(wsStart);
                        ends.Add(i);
                    }
                    continue;
                }

                // Join soft hyphenated English line wraps (e.g. "inter-\nnational" -> "international")
                if (ch == '-' && sb.Length > 0 && char.IsLetter(sb[^1]) && i + 1 < src.Length && (src[i + 1] == '\r' || src[i + 1] == '\n'))
                {
                    int j = i + 1;
                    while (j < src.Length && char.IsWhiteSpace(src[j])) j++;
                    if (j < src.Length && char.IsLetter(src[j]) && char.IsLower(src[j]))
                    {
                        i = j;
                        continue;
                    }
                }

                sb.Append(ch);
                starts.Add(i);
                ends.Add(i + 1);
                i++;
            }
            return (sb.ToString(), starts, ends);
        }

        var (normQ, _, _) = BuildNormalizedMap(query.Trim());
        if (normQ.Length == 0) return results;

        var (normText, mapStarts, mapEnds) = BuildNormalizedMap(pageText);
        if (normText.Length < normQ.Length) return results;

        int pos = 0;
        while (pos <= normText.Length - normQ.Length)
        {
            int idx = normText.IndexOf(normQ, pos, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) break;
            int origStart = mapStarts[idx];
            int origEnd = mapEnds[idx + normQ.Length - 1];
            if (origEnd > origStart)
                results.Add((origStart, origEnd - origStart));
            pos = idx + Math.Max(1, normQ.Length);
        }
        return results;
    }

    public static IReadOnlyList<(int PageZeroBased, int CharOffset, int MatchLen, string Snippet)> SearchText(string filePath, string query)
    {
        var hits = new List<(int, int, int, string)>();
        if (string.IsNullOrWhiteSpace(query)) return hits;
        string q = query.Trim();
        try
        {
            using var pig = UglyToad.PdfPig.PdfDocument.Open(filePath);
            for (int i = 1; i <= pig.NumberOfPages && hits.Count < 350; i++)
            {
                string text = ExtractPageLayoutCore(pig.GetPage(i), i - 1, includeGlyphs: false).Text;
                if (string.IsNullOrEmpty(text)) continue;
                var matches = FindNormalizedMatches(text, q);
                foreach (var (origStart, origLen) in matches)
                {
                    if (hits.Count >= 350) break;
                    int start = Math.Max(0, origStart - 24);
                    int end = Math.Min(text.Length, origStart + origLen + 32);
                    string snippet = text[start..end].Replace('\r', ' ').Replace('\n', ' ').Trim();
                    hits.Add((i - 1, origStart, origLen, snippet));
                }
            }
        }
        catch { }
        return hits;
    }
}
