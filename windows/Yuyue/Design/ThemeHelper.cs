using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using WinRT.Interop;

namespace Yuyue.Design;

/// <summary>
/// Theme, brush, shadow &amp; titlebar helpers backed by <see cref="YYColors"/>, <see cref="YYTints"/>, and <see cref="YYPapers"/>.
/// </summary>
public static class ThemeHelper
{
    public static bool IsSystemDark()
    {
        try
        {
            var bg = new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Background);
            return (bg.R + bg.G + bg.B) < 384;
        }
        catch
        {
            return Application.Current.RequestedTheme == ApplicationTheme.Dark;
        }
    }

    public static bool IsDark(ElementTheme theme) => theme switch
    {
        ElementTheme.Dark => true,
        ElementTheme.Light => false,
        _ => IsSystemDark(),
    };

    public static YYPalette Palette(bool dark) => dark ? YYColors.Dark : YYColors.Light;

    public static YYTintColors TintColors(YYTint tint, bool dark) => dark ? tint.Dark : tint.Light;

    public static SolidColorBrush Brush(Color color) => new(color);

    /// <summary>
    /// Writes WinUI 3 control theme resources (<c>Slider</c>, <c>ToggleSwitch</c>, <c>TextBox</c>, accent fills)
    /// directly onto a <see cref="ResourceDictionary"/> (app root or window <c>RootGrid.Resources</c>) so they
    /// cleanly override <c>XamlControlsResources</c> in both Light and Dark themes without shadowing one another.
    /// </summary>
    public static void ApplyControlThemeResources(ResourceDictionary target, bool dark, YYTint tint)
    {
        var tc = TintColors(tint, dark);
        static Color Shift(Color c, int delta) => Color.FromArgb(
            c.A,
            (byte)Math.Clamp(c.R + delta, 0, 255),
            (byte)Math.Clamp(c.G + delta, 0, 255),
            (byte)Math.Clamp(c.B + delta, 0, 255));

        Color accent = tc.Fill;
        Color accentHover = Shift(accent, dark ? 14 : -12);
        Color accentPress = Shift(accent, dark ? -10 : -24);

        void SetBrush(string key, Color color)
        {
            if (target.TryGetValue(key, out var existing) && existing is SolidColorBrush sb)
                sb.Color = color;
            else
                target[key] = new SolidColorBrush(color);
        }

        void SetColor(string key, Color color) => target[key] = color;

        // Accent
        SetColor("SystemAccentColor", accent);
        SetBrush("AccentFillColorDefaultBrush", accent);
        SetBrush("AccentFillColorSecondaryBrush", accentHover);
        SetBrush("AccentFillColorTertiaryBrush", accentPress);

        // Slider (macOS white thumb with subtle hairline border + soft warm unfilled track)
        SetBrush("SliderTrackFill", dark ? Color.FromArgb(255, 50, 51, 55) : Color.FromArgb(255, 228, 225, 219));
        SetBrush("SliderTrackFillPointerOver", dark ? Color.FromArgb(255, 60, 61, 66) : Color.FromArgb(255, 221, 218, 211));
        SetBrush("SliderTrackFillPressed", dark ? Color.FromArgb(255, 69, 70, 75) : Color.FromArgb(255, 213, 209, 201));
        SetBrush("SliderTrackValueFill", accent);
        SetBrush("SliderTrackValueFillPointerOver", accentHover);
        SetBrush("SliderTrackValueFillPressed", accentPress);
        SetBrush("SliderThumbBackground", Colors.White);
        SetBrush("SliderThumbBackgroundPointerOver", Colors.White);
        SetBrush("SliderThumbBackgroundPressed", dark ? Color.FromArgb(255, 237, 237, 238) : Color.FromArgb(255, 247, 245, 241));
        SetBrush("SliderOuterThumbBackground", dark ? Color.FromArgb(255, 242, 242, 243) : Colors.White);
        SetBrush("SliderThumbBorderBrush", dark ? Color.FromArgb(255, 68, 70, 76) : Color.FromArgb(255, 204, 200, 192));

        // TextBox clean 1px warm border
        SetBrush("TextControlBorderBrush", dark ? Color.FromArgb(255, 51, 52, 56) : Color.FromArgb(255, 226, 223, 218));
        SetBrush("TextControlBorderBrushPointerOver", dark ? Color.FromArgb(255, 70, 71, 76) : Color.FromArgb(255, 207, 203, 196));
        SetBrush("TextControlBorderBrushFocused", accent);
        target["TextControlBorderThemeThickness"] = new Thickness(1);
        target["TextControlBorderThemeThicknessFocused"] = new Thickness(1.5);

        // ToggleSwitch (borderless macOS pill; PointerOver/Pressed/Disabled OFF keys MUST be Color for LinearColorKeyFrame)
        Color transparent = Colors.Transparent;
        SetBrush("ToggleSwitchFillOn", accent);
        SetBrush("ToggleSwitchFillOnPointerOver", accentHover);
        SetBrush("ToggleSwitchFillOnPressed", accentPress);
        SetBrush("ToggleSwitchFillOnDisabled", dark ? Color.FromArgb(255, 44, 45, 48) : Color.FromArgb(255, 227, 224, 218));
        SetBrush("ToggleSwitchStrokeOn", transparent);
        SetBrush("ToggleSwitchStrokeOnPointerOver", transparent);
        SetBrush("ToggleSwitchStrokeOnPressed", transparent);
        SetBrush("ToggleSwitchStrokeOnDisabled", transparent);
        SetBrush("ToggleSwitchKnobFillOn", Colors.White);
        SetBrush("ToggleSwitchKnobFillOnPointerOver", Colors.White);
        SetBrush("ToggleSwitchKnobFillOnPressed", Colors.White);
        SetBrush("ToggleSwitchKnobFillOnDisabled", dark ? Color.FromArgb(255, 126, 124, 121) : Colors.White);

        SetBrush("ToggleSwitchFillOff", dark ? Color.FromArgb(255, 56, 57, 61) : Color.FromArgb(255, 221, 218, 212));
        SetBrush("ToggleSwitchFillOffPointerOver", dark ? Color.FromArgb(255, 67, 68, 73) : Color.FromArgb(255, 210, 206, 199));
        SetBrush("ToggleSwitchFillOffPressed", dark ? Color.FromArgb(255, 77, 78, 84) : Color.FromArgb(255, 199, 194, 186));
        SetBrush("ToggleSwitchFillOffDisabled", dark ? Color.FromArgb(255, 44, 45, 48) : Color.FromArgb(255, 228, 226, 220));
        SetBrush("ToggleSwitchStrokeOff", transparent);
        SetBrush("ToggleSwitchStrokeOffPointerOver", transparent);
        SetBrush("ToggleSwitchStrokeOffPressed", transparent);
        SetBrush("ToggleSwitchStrokeOffDisabled", transparent);
        SetBrush("ToggleSwitchKnobFillOff", dark ? Color.FromArgb(255, 237, 237, 238) : Colors.White);
        SetBrush("ToggleSwitchKnobFillOffPointerOver", Colors.White);
        SetBrush("ToggleSwitchKnobFillOffPressed", Colors.White);
        SetBrush("ToggleSwitchKnobFillOffDisabled", dark ? Color.FromArgb(255, 126, 124, 121) : Colors.White);
    }

    /// <summary>
    /// Elevates an element above a receiver using WinUI 3's native <see cref="ThemeShadow"/>.
    /// </summary>
    public static void ApplyElevation(UIElement target, UIElement receiver, float z = 16f)
    {
        try
        {
            var shadow = new ThemeShadow();
            shadow.Receivers.Add(receiver);
            target.Shadow = shadow;
            target.Translation = new Vector3(0, 0, z);
        }
        catch
        {
            // Best effort on older builds
        }
    }

    public static readonly (string Id, string Title)[] AppIcons =
    [
        ("default", "宣纸"),
        ("night", "暗夜"),
        ("camel", "暖纸"),
        ("sage", "草木"),
        ("mist", "烟雨"),
        ("blush", "海棠"),
    ];

    public static string NormalizeAppIconId(string? id) =>
        AppIcons.Any(x => x.Id == id) ? id! : "default";

    public static string GetAppIconTitle(string? id)
    {
        string norm = NormalizeAppIconId(id);
        return AppIcons.First(x => x.Id == norm).Title;
    }

    public static string ResolveAppIconAssetPath(string? iconId, bool dark, bool ico)
    {
        string norm = NormalizeAppIconId(iconId);
        string variant = dark ? "dark" : "light";
        string ext = ico ? "ico" : "png";
        string candidate = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcons", $"{norm}-{variant}.{ext}");
        if (File.Exists(candidate)) return candidate;
        return Path.Combine(AppContext.BaseDirectory, "Assets", ico ? "AppIcon.ico" : "AppIcon.png");
    }

    public static void ConfigureTitleBar(Window window, bool dark, Color? buttonHoverBg = null)
    {
        window.ExtendsContentIntoTitleBar = true;
        var appWindow = window.AppWindow;
        if (appWindow is null) return;

        try
        {
            string iconId = App.CurrentApp?.Library?.Prefs.AppIconId ?? "default";
            string iconPath = ResolveAppIconAssetPath(iconId, dark, ico: true);
            if (File.Exists(iconPath)) appWindow.SetIcon(iconPath);
        }
        catch { }

        if (!AppWindowTitleBar.IsCustomizationSupported()) return;

        var titleBar = appWindow.TitleBar;
        titleBar.ExtendsContentIntoTitleBar = true;
        titleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;

        var palette = Palette(dark);
        titleBar.ButtonForegroundColor = palette.T2;
        titleBar.ButtonInactiveForegroundColor = palette.T3;
        titleBar.ButtonHoverForegroundColor = palette.T1;
        titleBar.ButtonPressedForegroundColor = palette.T1;
        titleBar.ButtonHoverBackgroundColor = buttonHoverBg ?? (dark
            ? Color.FromArgb(20, 255, 255, 255)
            : Color.FromArgb(14, 42, 43, 46));
        titleBar.ButtonPressedBackgroundColor = dark
            ? Color.FromArgb(36, 255, 255, 255)
            : Color.FromArgb(24, 42, 43, 46);
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, nuint uIdSubclass, nuint dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, nuint uIdSubclass);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, nuint uIdSubclass, nuint dwRefData);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT_WIN32
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT_WIN32 ptReserved;
        public POINT_WIN32 ptMaxSize;
        public POINT_WIN32 ptMaxPosition;
        public POINT_WIN32 ptMinTrackSize;
        public POINT_WIN32 ptMaxTrackSize;
    }

    private const uint WM_GETMINMAXINFO = 0x0024;
    private const uint WM_NCDESTROY = 0x0082;
    private const nuint MinSizeSubclassId = 0x5959_4D49;
    private static readonly SubclassProc s_minSizeSubclassDelegate = MinSizeSubclassWndProc;
    private static readonly Dictionary<IntPtr, (int MinWidthDip, int MinHeightDip)> s_windowMinBounds = new();

    private static IntPtr MinSizeSubclassWndProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, nuint uIdSubclass, nuint dwRefData)
    {
        if (uMsg == WM_GETMINMAXINFO && lParam != IntPtr.Zero)
        {
            (int minWDip, int minHDip) bounds;
            bool found;
            lock (s_windowMinBounds) found = s_windowMinBounds.TryGetValue(hWnd, out bounds);
            if (found)
            {
                uint dpi = GetDpiForWindow(hWnd);
                double scale = dpi >= 96 ? dpi / 96.0 : 1.0;
                var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                mmi.ptMinTrackSize.X = Math.Max(mmi.ptMinTrackSize.X, (int)Math.Round(bounds.minWDip * scale));
                mmi.ptMinTrackSize.Y = Math.Max(mmi.ptMinTrackSize.Y, (int)Math.Round(bounds.minHDip * scale));
                Marshal.StructureToPtr(mmi, lParam, fDeleteOld: false);
                return IntPtr.Zero;
            }
        }
        else if (uMsg == WM_NCDESTROY)
        {
            lock (s_windowMinBounds) s_windowMinBounds.Remove(hWnd);
            RemoveWindowSubclass(hWnd, s_minSizeSubclassDelegate, uIdSubclass);
        }
        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    public static void SetWindowMinimumSize(Window window, int minWidthDip, int minHeightDip)
    {
        if (minWidthDip <= 0 || minHeightDip <= 0) return;
        try
        {
            IntPtr hwnd = WindowNative.GetWindowHandle(window);
            if (hwnd == IntPtr.Zero) return;
            bool isNew;
            lock (s_windowMinBounds)
            {
                isNew = !s_windowMinBounds.ContainsKey(hwnd);
                s_windowMinBounds[hwnd] = (minWidthDip, minHeightDip);
            }
            if (isNew)
            {
                SetWindowSubclass(hwnd, s_minSizeSubclassDelegate, MinSizeSubclassId, 0);
            }
        }
        catch { }
    }

    public static double GetWindowScale(Window window)
    {
        try
        {
            uint dpi = GetDpiForWindow(WindowNative.GetWindowHandle(window));
            if (dpi >= 96) return dpi / 96.0;
        }
        catch { }
        return 1.0;
    }

    internal static (int WidthDip, int HeightDip) PhysicalSizeToDip(int physicalWidth, int physicalHeight, uint dpi)
    {
        double scale = dpi >= 96 ? dpi / 96.0 : 1.0;
        return (
            Math.Max(320, (int)Math.Round(physicalWidth / scale)),
            Math.Max(240, (int)Math.Round(physicalHeight / scale)));
    }

    public static bool TryGetRestoredDipSize(Window window, out int widthDip, out int heightDip)
    {
        widthDip = 0;
        heightDip = 0;
        var appWindow = window.AppWindow;
        if (appWindow is null) return false;

        if (appWindow.Presenter is OverlappedPresenter op && op.State != OverlappedPresenterState.Restored)
            return false;
        if (appWindow.Presenter?.Kind == AppWindowPresenterKind.FullScreen)
            return false;

        uint dpi = 96;
        try
        {
            uint raw = GetDpiForWindow(WindowNative.GetWindowHandle(window));
            if (raw >= 96) dpi = raw;
        }
        catch { }

        var sz = appWindow.Size;
        if (sz.Width <= 200 || sz.Height <= 200) return false;
        (widthDip, heightDip) = PhysicalSizeToDip(sz.Width, sz.Height, dpi);
        return true;
    }

    public static void ResizeWindow(Window window, int widthDip, int heightDip, int minWidthDip = 0, int minHeightDip = 0)
    {
        var appWindow = window.AppWindow;
        if (appWindow is null) return;

        double scale = GetWindowScale(window);

        if (minWidthDip > 0 && minHeightDip > 0)
        {
            widthDip = Math.Max(widthDip, minWidthDip);
            heightDip = Math.Max(heightDip, minHeightDip);
            SetWindowMinimumSize(window, minWidthDip, minHeightDip);
        }

        if (appWindow.Presenter is OverlappedPresenter curOp && curOp.State == OverlappedPresenterState.Maximized)
            return;

        int pw = (int)Math.Round(widthDip * scale);
        int ph = (int)Math.Round(heightDip * scale);

        try
        {
            var area = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
            if (area is not null)
            {
                pw = Math.Min(pw, (int)(area.WorkArea.Width * 0.94));
                ph = Math.Min(ph, (int)(area.WorkArea.Height * 0.94));
            }
        }
        catch { }

        appWindow.Resize(new Windows.Graphics.SizeInt32(pw, ph));
    }
}
