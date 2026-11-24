using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.System;
using Windows.UI;
using Yuyue.Core;
using Yuyue.Design;
using Yuyue.Speech;
using XamlLine = Microsoft.UI.Xaml.Shapes.Line;

namespace Yuyue.Podcast;

public sealed partial class PodcastReaderWindow : Window
{
    private readonly LibraryService _library;
    private readonly string _issueId;
    private PodcastIssue _issue;
    private MagazineSentence[] _transcript = Array.Empty<MagazineSentence>();

    private readonly MediaPlayer _streamPlayer;
    private readonly AudioPlayer _ttsPlayer;
    private bool _playingStream;
    private bool _playingTts;
    private bool _transcribing;
    private string _transcribeStatus = "";
    private double _rate = 1.0;

    public PodcastReaderWindow(LibraryService library, string issueId)
    {
        _library = library;
        _issueId = issueId;
        _issue = library.Core.PodcastIssue(issueId);
        _rate = Math.Clamp(library.Prefs.SpeechRate, 0.75, 2.0);

        InitializeComponent();
        Title = _issue.Title;
        ThemeHelper.ResizeWindow(this, 1020, 680);
        SetTitleBar(TitleDragRegion);

        _streamPlayer = new MediaPlayer { AudioCategory = MediaPlayerAudioCategory.Speech, AutoPlay = false };
        _streamPlayer.MediaEnded += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            _playingStream = false;
            _library.SystemIntegration.SetAudioPlaying(this, false);
            UpdatePlayButton();
        });

        _ttsPlayer = new AudioPlayer(_library);
        try { App.CurrentApp.BindAudioClaimOwner(_ttsPlayer, this); } catch { }
        _ttsPlayer.SetRate(_rate);
        _ttsPlayer.SectionFinished += () => DispatcherQueue.TryEnqueue(() =>
        {
            _playingTts = false;
            _library.SystemIntegration.SetAudioPlaying(this, false);
            UpdatePlayButton();
        });

        Closed += OnWindowClosed;

        ApplyAppearance();
        RenderIssue();
        RenderClips();
    }

    public void ApplyAppearance()
    {
        RootGrid.RequestedTheme = _library.Prefs.ToElementTheme();
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        IconBack.Content = YYIcons.Make("chevron-left", 12, ThemeHelper.Brush(p.T1));
        RateBtn.Content = $"{_rate:0.##}×";
        ThemeHelper.ConfigureTitleBar(this, dark);
        L10n.Skip(ShowNameTopText);
        L10n.Skip(IssueTitleText);
        L10n.Skip(IssueSummaryText);
        UpdatePlayButton();
        L10n.ApplyTree(RootGrid);
    }

    private void RenderIssue()
    {
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);

        ShowNameTopText.Text = _issue.ShowTitle;
        IssueShowBadge.Text = _issue.Source == PodcastSourceKind.Studio ? $"✦ 书房电台 · {_issue.ShowTitle}" : _issue.ShowTitle;
        IssueTitleText.Text = _issue.Title;
        IssueSummaryText.Text = _issue.Summary;
        IssueSummaryText.Visibility = string.IsNullOrWhiteSpace(_issue.Summary) ? Visibility.Collapsed : Visibility.Visible;

        TranscriptStack.Children.Clear();
        _transcript = _library.Core.PodcastTranscript(_issueId);

        if (_transcript.Length > 0)
        {
            foreach (var line in _transcript)
            {
                if (!string.IsNullOrEmpty(line.SkipReason)) continue;
                var sent = line;

                var card = new Border
                {
                    CornerRadius = new CornerRadius(12),
                    Background = ThemeHelper.Brush(p.Inset),
                    Padding = new Thickness(14, 12, 14, 12),
                };

                var sp = new StackPanel { Spacing = 5 };
                var head = new Grid();
                if (!string.IsNullOrWhiteSpace(sent.Speaker))
                {
                    head.Children.Add(new TextBlock
                    {
                        Text = sent.Speaker,
                        FontSize = 11.5,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        Foreground = (Brush)Application.Current.Resources["YYAccentBrush"],
                        VerticalAlignment = VerticalAlignment.Center,
                    });
                }

                var clipBtn = new Button
                {
                    Style = (Style)Application.Current.Resources["YYIconButtonStyle"],
                    Content = "+ 摘录",
                    FontSize = 11,
                    Height = 22,
                    Padding = new Thickness(7, 0, 7, 0),
                    HorizontalAlignment = HorizontalAlignment.Right,
                };
                clipBtn.Click += (_, _) =>
                {
                    try
                    {
                        _library.Core.PodcastAddClip(_issueId, sent.T0, sent.T1, sent.Speaker, sent.TextOrig, null, null);
                        RenderClips();
                    }
                    catch { }
                };
                head.Children.Add(clipBtn);
                sp.Children.Add(head);

                sp.Children.Add(new TextBlock
                {
                    Text = sent.TextOrig,
                    FontSize = 14,
                    Foreground = ThemeHelper.Brush(p.T1),
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 23,
                });

                if (!string.IsNullOrWhiteSpace(sent.TextTrans))
                {
                    sp.Children.Add(new TextBlock
                    {
                        Text = sent.TextTrans,
                        FontSize = 12.5,
                        Foreground = ThemeHelper.Brush(p.T2),
                        TextWrapping = TextWrapping.Wrap,
                    });
                }

                card.Child = sp;
                TranscriptStack.Children.Add(card);
            }
        }
        else
        {
            var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);
            var serif = new FontFamily(YYFontFamily.Serif.WindowsFamily());
            var bannerGrid = new Grid { ColumnSpacing = 10 };
            bannerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bannerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bannerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var waveIcon = YYIcons.Make("wave", 14, ThemeHelper.Brush(tint.Accent));
            waveIcon.VerticalAlignment = VerticalAlignment.Center;
            bannerGrid.Children.Add(waveIcon);

            var statusMeta = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            statusMeta.Children.Add(new TextBlock
            {
                Text = L10n.T("原声播放中 · 文字稿尚未就绪"),
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = ThemeHelper.Brush(p.T1),
            });
            statusMeta.Children.Add(new TextBlock
            {
                Text = _transcribing && !string.IsNullOrWhiteSpace(_transcribeStatus)
                    ? _transcribeStatus
                    : L10n.T("随时可点击右上角收听原声，音频单集支持一键本机转文字。"),
                FontSize = 11.5,
                Foreground = ThemeHelper.Brush(_transcribing ? tint.Accent : p.T3),
                TextWrapping = TextWrapping.Wrap,
            });
            Grid.SetColumn(statusMeta, 1);
            bannerGrid.Children.Add(statusMeta);

            bool hasAudio = !string.IsNullOrWhiteSpace(_issue.LocalAudioPath) || !string.IsNullOrWhiteSpace(_issue.AudioUrl);
            if (hasAudio)
            {
                var trBtn = new Button
                {
                    Content = _transcribing ? L10n.T("转写中…") : L10n.T("本机转文字"),
                    Height = 26,
                    MinHeight = 26,
                    Padding = new Thickness(11, 0, 11, 0),
                    CornerRadius = new CornerRadius(13),
                    FontSize = 11.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = ThemeHelper.Brush(tint.Accent),
                    Background = ThemeHelper.Brush(tint.Soft),
                    BorderBrush = ThemeHelper.Brush(tint.Edge),
                    BorderThickness = new Thickness(1),
                    UseSystemFocusVisuals = false,
                    IsEnabled = !_transcribing,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                trBtn.Click += async (_, _) =>
                {
                    if (_transcribing) return;
                    _transcribing = true;
                    _transcribeStatus = L10n.T("正在准备单集音频…");
                    RenderIssue();
                    try
                    {
                        await _library.Podcasts.TranscribeIssueOnDeviceAsync(_issueId, msg =>
                        {
                            _transcribeStatus = msg;
                            DispatcherQueue.TryEnqueue(RenderIssue);
                        });
                        _issue = _library.Core.PodcastIssue(_issueId);
                    }
                    catch (Exception ex)
                    {
                        _transcribeStatus = ex.Message;
                    }
                    finally
                    {
                        _transcribing = false;
                        RenderIssue();
                    }
                };
                Grid.SetColumn(trBtn, 2);
                bannerGrid.Children.Add(trBtn);
            }

            TranscriptStack.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(10),
                Background = ThemeHelper.Brush(p.Inset),
                BorderBrush = ThemeHelper.Brush(p.Line),
                BorderThickness = new Thickness(0.5),
                Padding = new Thickness(14, 11, 14, 11),
                Child = bannerGrid,
            });

            if (!string.IsNullOrWhiteSpace(_issue.Shownotes))
            {
                TranscriptStack.Children.Add(new TextBlock
                {
                    Text = _issue.Shownotes,
                    FontFamily = serif,
                    FontSize = 14.5,
                    Foreground = ThemeHelper.Brush(p.T2),
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 24,
                    Margin = new Thickness(0, 4, 0, 0),
                });
            }
        }
    }

    private void RenderClips()
    {
        ClipsStack.Children.Clear();
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        var serif = new FontFamily(YYFontFamily.Serif.WindowsFamily());
        Color leafSurface = dark ? Color.FromArgb(255, 31, 30, 28) : Color.FromArgb(255, 248, 245, 239);

        PodcastClip[] clips;
        try { clips = _library.Core.PodcastClips(_issueId); }
        catch { clips = Array.Empty<PodcastClip>(); }

        if (clips.Length == 0)
        {
            ClipsStack.Children.Add(new TextBlock
            {
                Text = L10n.T("点击对话右上角「+ 摘录」，把打动你的段落留在这里。"),
                FontSize = 12,
                Foreground = ThemeHelper.Brush(p.T3),
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        foreach (var clip in clips)
        {
            var cid = clip.Id;
            var sp = new StackPanel { Spacing = 6 };
            if (!string.IsNullOrWhiteSpace(clip.Speaker))
                sp.Children.Add(new TextBlock { Text = clip.Speaker, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = (Brush)Application.Current.Resources["YYAccentBrush"] });
            sp.Children.Add(new TextBlock { Text = $"「{clip.QuoteText}」", FontFamily = serif, FontSize = 12.5, Foreground = ThemeHelper.Brush(p.T1), TextWrapping = TextWrapping.Wrap, LineHeight = 19 });

            var dashRule = new XamlLine
            {
                X1 = 0,
                X2 = 220,
                Y1 = 0,
                Y2 = 0,
                Stroke = ThemeHelper.Brush(p.Line),
                StrokeThickness = 0.75,
                StrokeDashArray = new DoubleCollection { 3, 3 },
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            sp.SizeChanged += (_, se) => { if (se.NewSize.Width > 8) dashRule.X2 = se.NewSize.Width; };
            sp.Children.Add(dashRule);

            var noteBox = new TextBox
            {
                Text = _library.Podcasts.GetClipThought(clip),
                PlaceholderText = L10n.T("添一句随想…"),
                FontSize = 11.5,
                Foreground = ThemeHelper.Brush(p.T2),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0, 2, 0, 2),
            };
            noteBox.LostFocus += (_, _) => _library.Podcasts.SetClipThought(cid, noteBox.Text);
            noteBox.KeyDown += (_, ke) =>
            {
                if (ke.Key == VirtualKey.Enter)
                {
                    ke.Handled = true;
                    _library.Podcasts.SetClipThought(cid, noteBox.Text);
                    RootGrid.Focus(FocusState.Programmatic);
                }
            };
            sp.Children.Add(noteBox);

            var del = new Button
            {
                Style = (Style)Application.Current.Resources["YYIconButtonStyle"],
                Content = L10n.T("删除"),
                FontSize = 11,
                Height = 20,
                Padding = new Thickness(6, 0, 6, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            del.Click += (_, _) => { try { _library.Core.PodcastDeleteClip(cid); RenderClips(); } catch { } };
            sp.Children.Add(del);

            ClipsStack.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(11),
                Background = ThemeHelper.Brush(leafSurface),
                BorderBrush = ThemeHelper.Brush(p.Line),
                BorderThickness = new Thickness(0.5),
                Padding = new Thickness(11, 9, 11, 8),
                Child = sp,
            });
        }
    }

    private void BackToShelf_Click(object sender, RoutedEventArgs e) => App.CurrentApp.ShowShelf();

    private void PauseFromAudioClaim()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_playingStream)
            {
                try { _streamPlayer.Pause(); } catch { }
                _playingStream = false;
            }
            if (_playingTts)
            {
                _ttsPlayer.Stop();
                _playingTts = false;
            }
            _library.SystemIntegration.SetAudioPlaying(this, false);
            UpdatePlayButton();
        });
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_playingStream)
        {
            _streamPlayer.Pause();
            _playingStream = false;
            _library.SystemIntegration.SetAudioPlaying(this, false);
            UpdatePlayButton();
            return;
        }
        if (_playingTts)
        {
            _ttsPlayer.Stop();
            _playingTts = false;
            _library.SystemIntegration.SetAudioPlaying(this, false);
            UpdatePlayButton();
            return;
        }

        App.CurrentApp.ClaimAudioPlayback(this, PauseFromAudioClaim);

        // 1. Prefer real audio stream when available
        string? audioSrc = !string.IsNullOrWhiteSpace(_issue.LocalAudioPath) && File.Exists(_issue.LocalAudioPath)
            ? _issue.LocalAudioPath
            : _issue.AudioUrl;

        if (!string.IsNullOrWhiteSpace(audioSrc) && Uri.TryCreate(audioSrc, UriKind.Absolute, out var uri))
        {
            _streamPlayer.Source = MediaSource.CreateFromUri(uri);
            _streamPlayer.PlaybackSession.PlaybackRate = _rate;
            if (_issue.PositionSec > 1)
                _streamPlayer.PlaybackSession.Position = System.TimeSpan.FromSeconds(_issue.PositionSec);
            _streamPlayer.Play();
            _playingStream = true;
            _library.SystemIntegration.SetAudioPlaying(this, true, _issue.Title, PauseFromAudioClaim);
            UpdatePlayButton();
            return;
        }

        // 2. Otherwise read the Studio dialogue script aloud via AudioPlayer
        if (_transcript.Length > 0)
        {
            var lines = _transcript
                .Where(t => string.IsNullOrEmpty(t.SkipReason) && !string.IsNullOrWhiteSpace(t.TextOrig))
                .Select(t => string.IsNullOrWhiteSpace(t.Speaker) ? t.TextOrig : $"{t.Speaker}：{t.TextOrig}")
                .ToArray();
            if (lines.Length > 0)
            {
                _ttsPlayer.SetMetadata(_issue.Title, _issue.ShowTitle);
                _ttsPlayer.SetRate(_rate);
                _ttsPlayer.Start(lines, 0);
                _playingTts = true;
                _library.SystemIntegration.SetAudioPlaying(this, true, _issue.Title, PauseFromAudioClaim);
                UpdatePlayButton();
            }
        }
    }

    private void Rate_Click(object sender, RoutedEventArgs e)
    {
        double[] rates = [0.8, 1.0, 1.25, 1.5, 1.8, 2.0];
        int i = Array.FindIndex(rates, r => Math.Abs(r - _rate) < 0.06);
        _rate = rates[(i + 1) % rates.Length];
        _streamPlayer.PlaybackSession.PlaybackRate = _rate;
        _ttsPlayer.SetRate(_rate);
        RateBtn.Content = $"{_rate:0.##}×";
    }

    private void UpdatePlayButton()
    {
        PlayBtn.Content = (_playingStream || _playingTts) ? L10n.Tr("⏸ 暂停", "⏸ Pause") : L10n.Tr("▶ 收听", "▶ Listen");
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        if (_playingStream)
        {
            try
            {
                double pos = _streamPlayer.PlaybackSession.Position.TotalSeconds;
                _library.Core.PodcastSavePlayback(_issueId, pos, null);
            }
            catch { }
        }
        try { _streamPlayer.Pause(); _streamPlayer.Dispose(); } catch { }
        _ttsPlayer.Dispose();
    }
}
