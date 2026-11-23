using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.UI;

namespace Yuyue.Design;

/// <summary>
/// Port of macOS <c>CoverHue.leadCard</c>: averages a cover image to a muted medium-dark tone
/// so the lead 在读 card adopts the book's own colour while keeping white text crisp.
/// </summary>
public static class CoverColorHelper
{
    private static readonly Dictionary<string, (double H, double S, double V)> Cache = new();

    public static Color FallbackLead(bool dark) =>
        dark ? Color.FromArgb(255, 58, 49, 41) : Color.FromArgb(255, 108, 88, 69);

    public static async Task<Color> LoadLeadCardColorAsync(string? coverPath, bool dark)
    {
        if (string.IsNullOrEmpty(coverPath) || !File.Exists(coverPath))
            return FallbackLead(dark);

        if (!Cache.TryGetValue(coverPath, out var hsv))
        {
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(coverPath);
                using var stream = await file.OpenReadAsync();
                var decoder = await BitmapDecoder.CreateAsync(stream);
                var transform = new BitmapTransform
                {
                    ScaledWidth = 16,
                    ScaledHeight = 24,
                    InterpolationMode = BitmapInterpolationMode.Linear,
                };
                var pixelData = await decoder.GetPixelDataAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    transform,
                    ExifOrientationMode.IgnoreExifOrientation,
                    ColorManagementMode.DoNotColorManage);
                var pixels = pixelData.DetachPixelData();
                long r = 0, g = 0, b = 0, n = 0;
                for (int i = 0; i + 3 < pixels.Length; i += 4)
                {
                    b += pixels[i];
                    g += pixels[i + 1];
                    r += pixels[i + 2];
                    n++;
                }
                if (n > 0)
                {
                    hsv = RgbToHsv(r / (double)n / 255.0, g / (double)n / 255.0, b / (double)n / 255.0);
                    Cache[coverPath] = hsv;
                }
                else
                {
                    return FallbackLead(dark);
                }
            }
            catch
            {
                return FallbackLead(dark);
            }
        }

        double s = Math.Max(hsv.S, 0.05);
        bool cool = hsv.H > 0.2 && hsv.H < 0.9;
        double satCap = cool ? 0.24 : 0.28;
        double finalS = Math.Min(s, satCap);
        double finalV = dark ? 0.26 : Math.Clamp(hsv.V, 0.36, 0.48);
        return HsvToColor(hsv.H, finalS, finalV);
    }

    private static (double H, double S, double V) RgbToHsv(double r, double g, double b)
    {
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double d = max - min;
        double h = 0;
        if (d > 1e-6)
        {
            if (max == r) h = ((g - b) / d + (g < b ? 6 : 0)) / 6.0;
            else if (max == g) h = ((b - r) / d + 2) / 6.0;
            else h = ((r - g) / d + 4) / 6.0;
        }
        double s = max > 1e-6 ? d / max : 0;
        return (h, s, max);
    }

    private static Color HsvToColor(double h, double s, double v)
    {
        double c = v * s;
        double x = c * (1 - Math.Abs((h * 6) % 2 - 1));
        double m = v - c;
        double r = 0, g = 0, b = 0;
        int seg = (int)(h * 6) % 6;
        switch (seg)
        {
            case 0: r = c; g = x; break;
            case 1: r = x; g = c; break;
            case 2: g = c; b = x; break;
            case 3: g = x; b = c; break;
            case 4: r = x; b = c; break;
            default: r = c; b = x; break;
        }
        return Color.FromArgb(255,
            (byte)Math.Clamp(Math.Round((r + m) * 255), 0, 255),
            (byte)Math.Clamp(Math.Round((g + m) * 255), 0, 255),
            (byte)Math.Clamp(Math.Round((b + m) * 255), 0, 255));
    }
}
