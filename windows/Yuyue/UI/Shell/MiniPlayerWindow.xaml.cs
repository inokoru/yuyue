using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.System;
using Yuyue.Core;
using Yuyue.Design;

namespace Yuyue.UI.Shell;

/// <summary>
/// Always-on-Top Windows 11 Acrylic Mini Player (PiP) for listening to ebooks, audiobooks, PDFs, or podcasts
/// while working in other desktop apps.
/// </summary>
public sealed partial class MiniPlayerWindow : Window
{
    private readonly LibraryService _library;
    private IMiniPlayerSource? _source;
    private string? _loadedCoverPath;

    public MiniPlayerWindow(LibraryService library)
    {
        _library = library;
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleDragArea);

        try
        {
            if (DesktopAcrylicController.IsSupported())
                SystemBackdrop = new DesktopAcrylicBackdrop();
        }
        catch { }

        ConfigurePresenter();
        ApplyAppearance();
        PositionBottomRight();

        Closed += (_, _) => DetachSource();
    }

    private void ConfigurePresenter()
    {
        try
        {
            var appWindow = AppWindow;
            if (appWindow is null) return;

            if (appWindow.Presenter is OverlappedPresenter op)
            {
                op.IsAlwaysOnTop = true;
                op.IsResizable = false;
                op.IsMaximizable = false;
                op.IsMinimizable = false;
                op.SetBorderAndTitleBar(true, false);
            }

            double scale = ThemeHelper.GetWindowScale(this);
            appWindow.Resize(new SizeInt32(
                (int)Math.Round(368 * scale),
                (int)Math.Round(132 * scale)));

            string iconPath = ThemeHelper.ResolveAppIconAssetPath(
                _library.Prefs.AppIconId,
                ThemeHelper.IsDark(_library.Prefs.ToElementTheme()),
                ico: true);
            if (File.Exists(iconPath)) appWindow.SetIcon(iconPath);
        }
        catch { }
    }

    private void PositionBottomRight()
    {
        try
        {
            var appWindow = AppWindow;
            if (appWindow is null) return;
            var area = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
            if (area is null) return;
            double scale = ThemeHelper.GetWindowScale(this);
            int margin = (int)Math.Round(20 * scale);
            int x = area.WorkArea.X + area.WorkArea.Width - appWindow.Size.Width - margin;
            int y = area.WorkArea.Y + area.WorkArea.Height - appWindow.Size.Height - margin;
            appWindow.Move(new PointInt32(Math.Max(area.WorkArea.X, x), Math.Max(area.WorkArea.Y, y)));
        }
        catch { }
    }

    public void BindSource(IMiniPlayerSource? source)
    {
        if (ReferenceEquals(_source, source))
        {
            RefreshUi();
            return;
        }
        DetachSource();
        _source = source;
        if (_source is not null)
            _source.StateChanged += OnSourceStateChanged;
        RefreshUi();
    }

    private void DetachSource()
    {
        if (_source is not null)
        {
            _source.StateChanged -= OnSourceStateChanged;
            _source = null;
        }
    }

    private void OnSourceStateChanged()
    {
        DispatcherQueue.TryEnqueue(RefreshUi);
    }

    public void ApplyAppearance()
    {
        Title = L10n.T("语阅 · 迷你播放器");
        var theme = _library.Prefs.ToElementTheme();
        RootGrid.RequestedTheme = theme;
        bool dark = ThemeHelper.IsDark(theme);
        ThemeHelper.ApplyControlThemeResources(RootGrid.Resources, dark, _library.Prefs.Tint);
        L10n.ApplyTree(RootGrid);

        var p = ThemeHelper.Palette(dark);
        // Semi-translucent warm card over DesktopAcrylicBackdrop
        CardShell.Background = ThemeHelper.Brush(dark
            ? Windows.UI.Color.FromArgb(228, 30, 28, 26)
            : Windows.UI.Color.FromArgb(232, 252, 250, 246));
        CardShell.BorderBrush = ThemeHelper.Brush(p.Line);

        JumpBackBtn.Content = YYIcons.Make("detach", 13, ThemeHelper.Brush(p.T2));
        CloseBtn.Content = YYIcons.Make("xmark", 12, ThemeHelper.Brush(p.T2));
        PrevBtn.Content = YYIcons.Make("chevron-left", 14, ThemeHelper.Brush(p.T1));
        NextBtn.Content = YYIcons.Make("chevron-right", 14, ThemeHelper.Brush(p.T1));
        CoverFallbackIcon.Content = YYIcons.Make("headphones", 24, ThemeHelper.Brush(p.T3));
        RefreshUi();
    }

    private void RefreshUi()
    {
        bool dark = ThemeHelper.IsDark(_library.Prefs.ToElementTheme());
        var tc = ThemeHelper.TintColors(_library.Prefs.Tint, dark);
        PlayPauseBtn.Background = ThemeHelper.Brush(tc.Fill);
        ProgressFill.Background = ThemeHelper.Brush(tc.Fill);

        if (_source is null)
        {
            _loadedCoverPath = null;
            CoverImage.Source = null;
            TitleText.Text = L10n.T("语阅 · 迷你听书");
            SubtitleText.Text = L10n.T("在书籍或有声书中开始朗读后将在此同步显示");
            RateBtn.Content = "1.0×";
            PlayPauseBtn.Content = YYIcons.Make("play", 13, ThemeHelper.Brush(Colors.White), filled: true);
            ProgressFill.Width = 0;
            CoverImage.Visibility = Visibility.Collapsed;
            CoverFallbackIcon.Visibility = Visibility.Visible;
            return;
        }

        TitleText.Text = string.IsNullOrWhiteSpace(_source.Title) ? L10n.T("正在播放") : _source.Title;
        SubtitleText.Text = string.IsNullOrWhiteSpace(_source.Subtitle) ? L10n.T("正在朗读…") : _source.Subtitle;
        RateBtn.Content = $"{_source.PlaybackRate:0.##}×";
        PlayPauseBtn.Content = YYIcons.Make(_source.IsPlaying ? "pause" : "play", 13, ThemeHelper.Brush(Colors.White), filled: true);

        double ratio = Math.Clamp(_source.ProgressRatio, 0.0, 1.0);
        double trackW = Math.Max(180, (ProgressFill.Parent as FrameworkElement)?.ActualWidth ?? 240);
        ProgressFill.Width = Math.Round(trackW * ratio);

        if (!string.Equals(_loadedCoverPath, _source.CoverPath, StringComparison.OrdinalIgnoreCase))
        {
            _loadedCoverPath = _source.CoverPath;
            if (!string.IsNullOrEmpty(_loadedCoverPath) && File.Exists(_loadedCoverPath))
            {
                try
                {
                    var bmp = new BitmapImage { DecodePixelWidth = 160 };
                    bmp.UriSource = new Uri(_loadedCoverPath);
                    CoverImage.Source = bmp;
                    CoverImage.Visibility = Visibility.Visible;
                    CoverFallbackIcon.Visibility = Visibility.Collapsed;
                }
                catch
                {
                    CoverImage.Visibility = Visibility.Collapsed;
                    CoverFallbackIcon.Visibility = Visibility.Visible;
                }
            }
            else
            {
                CoverImage.Visibility = Visibility.Collapsed;
                CoverFallbackIcon.Visibility = Visibility.Visible;
            }
        }
    }

    private void SafeInvokeSource(Action<IMiniPlayerSource> action)
    {
        if (_source is null) return;
        try { action(_source); }
        catch (ObjectDisposedException)
        {
            App.CurrentApp.ClearActiveMiniPlayerSource(_source);
            BindSource(null);
        }
        catch { }
    }

    private void RootGrid_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Space)
        {
            SafeInvokeSource(s => s.TogglePlayPause());
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Left)
        {
            SafeInvokeSource(s => s.SkipPrevious());
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Right)
        {
            SafeInvokeSource(s => s.SkipNext());
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e) => SafeInvokeSource(s => s.TogglePlayPause());
    private void Prev_Click(object sender, RoutedEventArgs e) => SafeInvokeSource(s => s.SkipPrevious());
    private void Next_Click(object sender, RoutedEventArgs e) => SafeInvokeSource(s => s.SkipNext());
    private void Rate_Click(object sender, RoutedEventArgs e) => SafeInvokeSource(s => s.CycleRate());
    private void JumpBack_Click(object sender, RoutedEventArgs e) => SafeInvokeSource(s => s.JumpToSource());
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
