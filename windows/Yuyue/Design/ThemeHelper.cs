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
    public static bool IsDark(ElementTheme theme) => theme switch
    {
        ElementTheme.Dark => true,
        ElementTheme.Light => false,
        _ => Application.Current.RequestedTheme == ApplicationTheme.Dark,
    };

    public static YYPalette Palette(bool dark) => dark ? YYColors.Dark : YYColors.Light;

    public static YYTintColors TintColors(YYTint tint, bool dark) => dark ? tint.Dark : tint.Light;

    public static SolidColorBrush Brush(Color color) => new(color);

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

    public static void ConfigureTitleBar(Window window, bool dark, Color? buttonHoverBg = null)
    {
        window.ExtendsContentIntoTitleBar = true;
        var appWindow = window.AppWindow;
        if (appWindow is null) return;

        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
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

    public static void ResizeWindow(Window window, int widthDip, int heightDip)
    {
        var appWindow = window.AppWindow;
        if (appWindow is null) return;

        double scale = 1.0;
        try
        {
            uint dpi = GetDpiForWindow(WindowNative.GetWindowHandle(window));
            if (dpi >= 96) scale = dpi / 96.0;
        }
        catch { }

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
