using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Yuyue.Core;
using Yuyue.Design;
using Yuyue.Speech;

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

        _streamPlayer = new MediaPlayer { AutoPlay = false };
        _streamPlayer.MediaEnded += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            _playingStream = false;
            UpdatePlayButton();
        });

        _ttsPlayer = new AudioPlayer(_library);
        _ttsPlayer.SetRate(_rate);
        _ttsPlayer.SectionFinished += () => DispatcherQueue.TryEnqueue(() =>
        {
            _playingTts = false;
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
        else if (!string.IsNullOrWhiteSpace(_issue.Shownotes))
        {
            TranscriptStack.Children.Add(new TextBlock
            {
                Text = _issue.Shownotes,
                FontSize = 14,
                Foreground = ThemeHelper.Brush(p.T1),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 24,
            });
        }
        else
        {
            TranscriptStack.Children.Add(new TextBlock
            {
                Text = "本集暂无逐字稿，可点击右上角「▶ 收听」直接播放节目音频。",
                FontSize = 13,
                Foreground = ThemeHelper.Brush(p.T3),
            });
        }
    }

    private void RenderClips()
    {
        ClipsStack.Children.Clear();
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);

        PodcastClip[] clips;
        try { clips = _library.Core.PodcastClips(_issueId); }
        catch { clips = Array.Empty<PodcastClip>(); }

        if (clips.Length == 0)
        {
            ClipsStack.Children.Add(new TextBlock
            {
                Text = "点击对话右上角「+ 摘录」，把打动你的段落留在这里。",
                FontSize = 12,
                Foreground = ThemeHelper.Brush(p.T3),
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        foreach (var clip in clips)
        {
            var cid = clip.Id;
            var sp = new StackPanel { Spacing = 4 };
            if (!string.IsNullOrWhiteSpace(clip.Speaker))
                sp.Children.Add(new TextBlock { Text = clip.Speaker, FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = (Brush)Application.Current.Resources["YYAccentBrush"] });
            sp.Children.Add(new TextBlock { Text = clip.QuoteText, FontSize = 12, Foreground = ThemeHelper.Brush(p.T1), TextWrapping = TextWrapping.Wrap });

            var del = new Button
            {
                Style = (Style)Application.Current.Resources["YYIconButtonStyle"],
                Content = "删除",
                FontSize = 11,
                Height = 20,
                Padding = new Thickness(6, 0, 6, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            del.Click += (_, _) => { try { _library.Core.PodcastDeleteClip(cid); RenderClips(); } catch { } };
            sp.Children.Add(del);

            ClipsStack.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(10),
                Background = ThemeHelper.Brush(p.Inset),
                Padding = new Thickness(10, 8, 10, 6),
                Child = sp,
            });
        }
    }

    private void BackToShelf_Click(object sender, RoutedEventArgs e) => App.CurrentApp.ShowShelf();

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_playingStream)
        {
            _streamPlayer.Pause();
            _playingStream = false;
            UpdatePlayButton();
            return;
        }
        if (_playingTts)
        {
            _ttsPlayer.Stop();
            _playingTts = false;
            UpdatePlayButton();
            return;
        }

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
            UpdatePlayButton();
            return;
        }

        // 2. Otherwise read the Studio dialogue script aloud via AudioPlayer (neural ZipVoice or system TTS)
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
        PlayBtn.Content = (_playingStream || _playingTts) ? "⏸ 暂停" : "▶ 收听";
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
