using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;
using Yuyue.Core;
using Yuyue.Design;
using TimeSpan = System.TimeSpan;

namespace Yuyue.Audiobook;

/// <summary>
/// 1:1 port of <c>apple/Yuyue/Library/AudiobookScreen.swift</c>:
/// open warm-paper stage on the left (<c>240×240</c> square cover, chapter title above <c>Book · Author</c>,
/// per-chapter scrubber with bookmark ticks, 5 transport controls, keyboard keycap bar) and
/// <c>300px</c> <c>ChapterColumn</c> (chapters + bookmarks) + <c>pairCard</c> on the right.
/// </summary>
public sealed partial class AudiobookWindow : Window
{
    private readonly LibraryService _library;
    private readonly string _bookId;
    private BookSummary _summary;
    private AudioRecording? _recording;

    private readonly MediaPlayer _player;
    private readonly DispatcherTimer _uiTimer;
    private readonly DispatcherTimer _listenTimeTracker;
    private DispatcherTimer? _toastTimer;

    private double[] _fileStartOffsets = [];
    private int _currentFileIndex = -1;
    private double _globalPosSec;
    private bool _playing;
    private bool _suppressSlider;
    private double _rate = 1.0;

    // Sleep timer: 0 = off, -1 = end of chapter, >0 = remaining minutes
    private int _sleepMode;
    private DateTime? _sleepUntilUtc;

    private static readonly double[] SpeedOptions = [0.75, 1.0, 1.1, 1.2, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0];

    public AudiobookWindow(LibraryService library, string bookId, bool autoPlay = false)
    {
        _library = library;
        _bookId = bookId;
        _summary = library.GetKeptBooks().FirstOrDefault(b => b.Id == bookId)
            ?? library.GetBookDetail(bookId).Summary;
        _rate = Math.Clamp(_library.Prefs.SpeechRate, 0.75, 3.0);

        InitializeComponent();
        Title = _summary.Title;
        ThemeHelper.ResizeWindow(this, 960, 640);
        SetTitleBar(TitleDragRegion);
        ThemeHelper.ApplyElevation(CoverFrame, ShadowReceiver, 18f);
        ThemeHelper.ApplyElevation(ToastBanner, ShadowReceiver, 10f);

        _player = new MediaPlayer
        {
            AudioCategory = MediaPlayerAudioCategory.Speech,
            AutoPlay = false,
        };
        _player.MediaEnded += OnFileEnded;

        var smtc = _player.SystemMediaTransportControls;
        smtc.IsEnabled = true;
        smtc.IsPlayEnabled = true;
        smtc.IsPauseEnabled = true;
        smtc.IsNextEnabled = true;
        smtc.IsPreviousEnabled = true;
        smtc.ButtonPressed += OnSmtcButtonPressed;

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _uiTimer.Tick += OnUiTick;
        _uiTimer.Start();

        _listenTimeTracker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _listenTimeTracker.Tick += (_, _) =>
        {
            if (_playing)
            {
                try { _library.AddTime(_bookId, (uint)CurrentChapterIndex(), 0, 30); } catch { }
            }
        };
        _listenTimeTracker.Start();

        BookmarkTicksCanvas.SizeChanged += (_, _) => RenderBookmarkTicks();
        Closed += OnWindowClosed;

        ApplyAppearance();
        _ = LoadRecordingAsync(autoPlay);
    }

    public void ApplyAppearance()
    {
        RootGrid.RequestedTheme = _library.Prefs.ToElementTheme();
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);
        var t1 = ThemeHelper.Brush(p.T1);
        var t2 = ThemeHelper.Brush(p.T2);

        IconBack.Content = YYIcons.Make("chevron-left", 11.5, t1);
        CoverFallbackIcon.Content = YYIcons.Make("headphones", 46, ThemeHelper.Brush(p.T3));
        RateBtn.Content = $"{_rate:0.##}×";
        RateBtn.Foreground = t1;
        Rewind15Btn.Content = YYIcons.Make("goback-15", 19, t1);
        Forward30Btn.Content = YYIcons.Make("goforward-30", 19, t1);
        SleepBtn.Content = YYIcons.Make("moon", 16, _sleepMode != 0 ? ThemeHelper.Brush(tint.Accent) : t2, filled: _sleepMode != 0);

        UpdateTransportUi();
        ThemeHelper.ConfigureTitleBar(this, dark);
        RenderChaptersAndBookmarks();
        RenderPairCard();
    }

    private async Task LoadRecordingAsync(bool autoPlay)
    {
        string author = _summary.Authors.FirstOrDefault() ?? "";
        BookBylineLabel.Text = string.IsNullOrWhiteSpace(author) ? _summary.Title : $"{_summary.Title} · {author}";

        if (!string.IsNullOrEmpty(_summary.CoverPath) && File.Exists(_summary.CoverPath))
        {
            CoverImage.Source = new BitmapImage(new Uri(_summary.CoverPath));
            CoverFallbackIcon.Visibility = Visibility.Collapsed;
        }
        else
        {
            CoverFallbackIcon.Visibility = Visibility.Visible;
        }

        try
        {
            _recording = _library.Core.AudioRecording(_bookId);
        }
        catch (Exception ex)
        {
            ChapterLabel.Text = $"无法读取音频文件：{ex.Message}";
            return;
        }

        _fileStartOffsets = new double[_recording.Files.Length];
        double acc = 0;
        for (int i = 0; i < _recording.Files.Length; i++)
        {
            _fileStartOffsets[i] = acc;
            acc += _recording.Files[i].Seconds;
        }

        double totalSec = Math.Max(1.0, _recording.Seconds);
        var saved = _library.GetProgress(_bookId);
        if (saved is not null && _recording.Chapters.Length > 0)
        {
            int ch = Math.Clamp((int)saved.Position.Section, 0, _recording.Chapters.Length - 1);
            _globalPosSec = Math.Clamp(_recording.Chapters[ch].Start + saved.Position.Sentence / 1000.0, 0, totalSec);
        }

        await SeekGlobalAsync(_globalPosSec, startPlaying: autoPlay);
        SaveCurrentProgress();
        App.CurrentApp.NotifyShelfRefresh();
        RenderChaptersAndBookmarks();
        RenderPairCard();
    }

    private int CurrentChapterIndex()
    {
        if (_recording is null || _recording.Chapters.Length == 0) return 0;
        int hit = 0;
        for (int i = 0; i < _recording.Chapters.Length; i++)
        {
            if (_globalPosSec + 0.25 >= _recording.Chapters[i].Start) hit = i;
            else break;
        }
        return hit;
    }

    private string ChapterTitle(int index)
    {
        if (_recording is null || index < 0 || index >= _recording.Chapters.Length) return _summary.Title;
        var raw = _recording.Chapters[index].Title;
        return string.IsNullOrWhiteSpace(raw) ? $"第 {index + 1} 集" : raw;
    }

    private (double Start, double Duration) CurrentChapterBounds()
    {
        if (_recording is null || _recording.Chapters.Length == 0)
            return (0, Math.Max(1.0, _recording?.Seconds ?? 1.0));
        int idx = CurrentChapterIndex();
        var ch = _recording.Chapters[idx];
        return (ch.Start, Math.Max(1.0, ch.Seconds));
    }

    private async Task SeekGlobalAsync(double targetSec, bool startPlaying)
    {
        if (_recording is null || _recording.Files.Length == 0) return;
        double clamped = Math.Clamp(targetSec, 0, Math.Max(0, _recording.Seconds - 0.1));
        _globalPosSec = clamped;

        int fileIdx = 0;
        for (int i = 0; i < _fileStartOffsets.Length; i++)
        {
            if (clamped >= _fileStartOffsets[i]) fileIdx = i;
            else break;
        }

        double localSec = Math.Max(0, clamped - _fileStartOffsets[fileIdx]);
        if (fileIdx != _currentFileIndex)
        {
            string filePath = _recording.Files[fileIdx].Path;
            if (!File.Exists(filePath)) return;
            var sf = await StorageFile.GetFileFromPathAsync(filePath);
            var ms = MediaSource.CreateFromStorageFile(sf);
            await ms.OpenAsync();
            _player.Source = ms;
            _currentFileIndex = fileIdx;
        }

        _player.PlaybackSession.PlaybackRate = _rate;
        _player.PlaybackSession.Position = TimeSpan.FromSeconds(localSec);

        if (startPlaying)
        {
            _player.Play();
            _playing = true;
        }

        SyncChapterUi();
        UpdateTransportUi();
        SaveCurrentProgress();
    }

    private void OnFileEnded(MediaPlayer sender, object args)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (_recording is null) return;
            if (_currentFileIndex + 1 < _recording.Files.Length)
            {
                await SeekGlobalAsync(_fileStartOffsets[_currentFileIndex + 1], startPlaying: true);
                RenderChaptersAndBookmarks();
            }
            else
            {
                _playing = false;
                UpdateTransportUi();
                SaveCurrentProgress();
            }
        });
    }

    private void OnUiTick(object? sender, object e)
    {
        if (_recording is null) return;
        int prevChap = CurrentChapterIndex();

        if (_playing && _currentFileIndex >= 0 && _currentFileIndex < _fileStartOffsets.Length)
        {
            _globalPosSec = _fileStartOffsets[_currentFileIndex] + _player.PlaybackSession.Position.TotalSeconds;
            try
            {
                var moment = _library.Core.AudiobookMoment(_bookId, _globalPosSec);
                if (moment.SkipTo is double skipTo && skipTo > _globalPosSec + 0.6)
                {
                    double savedDelta = skipTo - _globalPosSec;
                    try { _library.Core.AudiobookAddSaved(_bookId, savedDelta); } catch { }
                    _ = SeekGlobalAsync(skipTo, startPlaying: true);
                }
            }
            catch { }
        }

        int curChap = CurrentChapterIndex();
        if (curChap != prevChap)
        {
            if (_sleepMode == -1)
            {
                PausePlayback();
                _sleepMode = 0;
                SleepCountdownText.Text = "";
                ApplyAppearance();
                AddBookmarkAtCurrentPosition(silent: true);
                ShowToast("本集播完 · 已自动加书签");
                return;
            }
            RenderChaptersAndBookmarks();
        }

        if (_sleepUntilUtc is DateTime until)
        {
            var rem = until - DateTime.UtcNow;
            if (rem.TotalSeconds <= 0)
            {
                PausePlayback();
                _sleepUntilUtc = null;
                _sleepMode = 0;
                SleepCountdownText.Text = "";
                ApplyAppearance();
                AddBookmarkAtCurrentPosition(silent: true);
                ShowToast("定时到了 · 已自动加书签");
            }
            else
            {
                SleepCountdownText.Text = $"定时还剩 {(int)rem.TotalMinutes:D2}:{rem.Seconds:D2}";
            }
        }

        SyncChapterUi();
    }

    private void SyncChapterUi()
    {
        if (_recording is null) return;
        int curChap = CurrentChapterIndex();
        var (chStart, chDur) = CurrentChapterBounds();
        double inChap = Math.Clamp(_globalPosSec - chStart, 0, chDur);

        ChapterLabel.Text = ChapterTitle(curChap);
        _suppressSlider = true;
        ChapterSlider.Maximum = chDur;
        ChapterSlider.Value = inChap;
        _suppressSlider = false;

        ChapterElapsedText.Text = FormatTime(inChap);
        ChapterTotalText.Text = FormatTime(chDur);

        double remBookSec = Math.Max(0, _recording.Seconds - _globalPosSec) / Math.Max(0.5, _rate);
        if (_recording.Chapters.Length > 1 && remBookSec >= 60)
        {
            int hrs = (int)(remBookSec / 3600);
            int mins = (int)Math.Round((remBookSec % 3600) / 60.0);
            BookRemainingText.Text = hrs > 0 ? $"全书还剩 {hrs} 小时 {mins} 分" : $"全书还剩 {mins} 分钟";
        }
        else
        {
            BookRemainingText.Text = "";
        }

        RenderBookmarkTicks();
    }

    private void RenderBookmarkTicks()
    {
        BookmarkTicksCanvas.Children.Clear();
        if (_recording is null || BookmarkTicksCanvas.ActualWidth <= 20) return;
        int curChap = CurrentChapterIndex();
        var (_, chDur) = CurrentChapterBounds();
        if (chDur <= 0) return;

        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        var bms = _library.GetBookmarks(_bookId).Where(b => (int)b.Section == curChap).ToArray();
        double w = BookmarkTicksCanvas.ActualWidth;

        foreach (var bm in bms)
        {
            double sec = bm.Sentence / 1000.0;
            double ratio = Math.Clamp(sec / chDur, 0.01, 0.99);
            var tick = new Border
            {
                Width = 2.5,
                Height = 8,
                CornerRadius = new CornerRadius(1.25),
                Background = ThemeHelper.Brush(p.Bg),
                BorderBrush = ThemeHelper.Brush(p.T1),
                BorderThickness = new Thickness(0.5),
            };
            Canvas.SetLeft(tick, ratio * w - 1.25);
            Canvas.SetTop(tick, 1);
            BookmarkTicksCanvas.Children.Add(tick);
        }
    }

    private void UpdateTransportUi()
    {
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        PlayPauseBtn.Background = ThemeHelper.Brush(p.T1);
        PlayPauseBtn.Content = YYIcons.Make(_playing ? "pause" : "play", 20, ThemeHelper.Brush(p.Bg), filled: true);

        try
        {
            var u = _player.SystemMediaTransportControls.DisplayUpdater;
            u.Type = MediaPlaybackType.Music;
            u.MusicProperties.Title = ChapterTitle(CurrentChapterIndex());
            u.MusicProperties.Artist = _summary.Title;
            u.Update();
        }
        catch { }
    }

    private void PausePlayback()
    {
        _playing = false;
        try { _player.Pause(); } catch { }
        UpdateTransportUi();
        SaveCurrentProgress();
    }

    private void SaveCurrentProgress()
    {
        if (_recording is null || _recording.Chapters.Length == 0) return;
        int ch = CurrentChapterIndex();
        uint ms = (uint)Math.Max(0, Math.Round((_globalPosSec - _recording.Chapters[ch].Start) * 1000.0));
        try { _library.SaveProgress(_bookId, (uint)ch, ms); } catch { }
    }

    // ---------- Right Column: ChapterColumn (Chapters + Bookmarks) & PairCard ----------

    private void RenderChaptersAndBookmarks()
    {
        ChapterListPanel.Children.Clear();
        if (_recording is null) return;

        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);
        int active = CurrentChapterIndex();

        for (int i = 0; i < _recording.Chapters.Length; i++)
        {
            if (i > 0) ChapterListPanel.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(12, 0, 12, 0) });
            int idx = i;
            var ch = _recording.Chapters[i];
            bool isCur = idx == active;
            bool isPast = idx < active;

            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            row.Children.Add(new TextBlock
            {
                Text = ChapterTitle(idx),
                FontSize = 12.5,
                FontWeight = isCur ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                Foreground = ThemeHelper.Brush(isCur ? tint.Accent : (isPast ? p.T3 : p.T1)),
                VerticalAlignment = VerticalAlignment.Center,
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });

            string timeStr = isCur
                ? $"还剩 {FormatTime(Math.Max(0, ch.Start + ch.Seconds - _globalPosSec))}"
                : FormatTime(ch.Seconds);
            var durTb = new TextBlock
            {
                Text = timeStr,
                FontSize = 11,
                Foreground = ThemeHelper.Brush(isCur ? tint.Accent : p.T3),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(durTb, 1);
            row.Children.Add(durTb);

            var ctx = new MenuFlyout();
            var renItem = new MenuFlyoutItem { Text = "改名…" };
            renItem.Click += async (_, _) => await RenameChapterAsync(idx);
            ctx.Items.Add(renItem);

            var itemBtn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = isCur ? ThemeHelper.Brush(tint.Soft) : new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(7),
                Margin = new Thickness(6, 1, 6, 1),
                Padding = new Thickness(8, 8, 8, 8),
                UseSystemFocusVisuals = false,
                ContextFlyout = ctx,
                Content = row,
            };
            itemBtn.Click += async (_, _) =>
            {
                await SeekGlobalAsync(ch.Start, startPlaying: true);
                RenderChaptersAndBookmarks();
            };
            ChapterListPanel.Children.Add(itemBtn);
        }

        // Bookmarks inside the same ChapterColumn card (AudiobookScreen.swift lines 935-968)
        var bms = _library.GetBookmarks(_bookId);
        if (bms.Count > 0)
        {
            ChapterListPanel.Children.Add(new Border { Height = 1, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(12, 6, 12, 4) });
            ChapterListPanel.Children.Add(new TextBlock
            {
                Text = "书签",
                FontSize = 10.5,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = ThemeHelper.Brush(p.T3),
                Margin = new Thickness(14, 4, 14, 2),
            });

            foreach (var bm in bms)
            {
                int chIdx = Math.Clamp((int)bm.Section, 0, Math.Max(0, _recording.Chapters.Length - 1));
                double inChSec = bm.Sentence / 1000.0;
                double targetGlobal = (_recording.Chapters.Length > chIdx ? _recording.Chapters[chIdx].Start : 0) + inChSec;

                var bRow = new Grid { ColumnSpacing = 8 };
                bRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
                bRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                bRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                bRow.Children.Add(YYIcons.Make("bookmark", 11.5, ThemeHelper.Brush(tint.Accent), filled: true));

                var titleTb = new TextBlock { Text = ChapterTitle(chIdx), FontSize = 12, Foreground = ThemeHelper.Brush(p.T1), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(titleTb, 1);
                bRow.Children.Add(titleTb);

                var atTb = new TextBlock { Text = FormatTime(inChSec), FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(atTb, 2);
                bRow.Children.Add(atTb);

                var ctx = new MenuFlyout();
                string bmId = bm.Id;
                var delItem = new MenuFlyoutItem { Text = "删除书签", Foreground = ThemeHelper.Brush(p.Danger) };
                delItem.Click += (_, _) =>
                {
                    _library.DeleteBookmark(_bookId, bmId);
                    RenderChaptersAndBookmarks();
                    RenderBookmarkTicks();
                };
                ctx.Items.Add(delItem);

                var bmBtn = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Background = new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    CornerRadius = new CornerRadius(7),
                    Margin = new Thickness(6, 1, 6, 1),
                    Padding = new Thickness(8, 6, 8, 6),
                    UseSystemFocusVisuals = false,
                    ContextFlyout = ctx,
                    Content = bRow,
                };
                bmBtn.Click += async (_, _) => await SeekGlobalAsync(targetGlobal, startPlaying: true);
                ChapterListPanel.Children.Add(bmBtn);
            }
        }
    }

    private void RenderPairCard()
    {
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);

        ListenPair? activePair = null;
        try { activePair = _library.Core.ListeningPairs().FirstOrDefault(lp => lp.Recording == _bookId); }
        catch { }

        var shelfEbooks = _library.GetShelfBooks().Where(b => b.Id != _bookId && b.Format != BookFormat.Audio).ToArray();
        BookSummary? pairedEbook = activePair is not null
            ? shelfEbooks.FirstOrDefault(b => b.Id == activePair.Book)
            : null;
        BookSummary? candidateEbook = pairedEbook is null
            ? AudiobookImporter.FindMatchingShelfEbook(_library, _summary.Title)
            : null;

        var g = new Grid { ColumnSpacing = 10 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (pairedEbook is not null)
        {
            var cov = new Border { Width = 24, Height = 34, CornerRadius = new CornerRadius(3), Background = ThemeHelper.Brush(p.Inset) };
            if (!string.IsNullOrEmpty(pairedEbook.CoverPath) && File.Exists(pairedEbook.CoverPath))
                cov.Child = new Image { Source = new BitmapImage(new Uri(pairedEbook.CoverPath)), Stretch = Stretch.UniformToFill };
            g.Children.Add(cov);

            var mid = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            mid.Children.Add(new TextBlock { Text = $"《{pairedEbook.Title}》", FontSize = 11.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
            mid.Children.Add(new TextBlock { Text = activePair?.Made == true ? "已配对 · 可切到书页同步阅读" : "已配对电子书 · 右键可换书或解绑", FontSize = 10, Foreground = ThemeHelper.Brush(tint.Accent), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(mid, 1);
            g.Children.Add(mid);

            string eid = pairedEbook.Id;
            var openBtn = new Button
            {
                Content = "切到书页",
                Height = 26,
                Padding = new Thickness(11, 0, 11, 0),
                CornerRadius = new CornerRadius(13),
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Background = ThemeHelper.Brush(p.T1),
                Foreground = ThemeHelper.Brush(p.Bg),
                BorderThickness = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            openBtn.Click += (_, _) => OpenSyncedPairedEbook(eid);
            Grid.SetColumn(openBtn, 2);
            g.Children.Add(openBtn);

            var ctx = new MenuFlyout();
            var pickOtherMi = new MenuFlyoutItem { Text = "换一本配对电子书…" };
            pickOtherMi.Click += async (_, _) => await ShowEbookPairPickerAsync();
            var unpairMi = new MenuFlyoutItem { Text = "解除配对", Foreground = ThemeHelper.Brush(p.Danger) };
            unpairMi.Click += (_, _) =>
            {
                try { _library.Core.AudiobookPair(_bookId, null); } catch { }
                RenderPairCard();
                ShowToast("已解除电子书配对");
            };
            ctx.Items.Add(pickOtherMi);
            ctx.Items.Add(new MenuFlyoutSeparator());
            ctx.Items.Add(unpairMi);
            PairCardHost.ContextFlyout = ctx;
        }
        else if (candidateEbook is not null)
        {
            PairCardHost.ContextFlyout = null;
            var cov = new Border { Width = 24, Height = 34, CornerRadius = new CornerRadius(3), Background = ThemeHelper.Brush(p.Inset) };
            if (!string.IsNullOrEmpty(candidateEbook.CoverPath) && File.Exists(candidateEbook.CoverPath))
                cov.Child = new Image { Source = new BitmapImage(new Uri(candidateEbook.CoverPath)), Stretch = Stretch.UniformToFill };
            g.Children.Add(cov);

            var mid = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            mid.Children.Add(new TextBlock { Text = $"发现《{candidateEbook.Title}》电子书", FontSize = 11.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
            mid.Children.Add(new TextBlock { Text = "一键合成听读书，进度互通", FontSize = 10, Foreground = ThemeHelper.Brush(p.T3), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(mid, 1);
            g.Children.Add(mid);

            string cid = candidateEbook.Id;
            var pairBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYPrimaryPillButtonStyle"],
                Content = "配对",
                Height = 26,
                MinHeight = 26,
                Padding = new Thickness(12, 0, 12, 0),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
            };
            pairBtn.Click += (_, _) =>
            {
                try { _library.Core.AudiobookPair(_bookId, cid); } catch { }
                RenderPairCard();
                ShowToast($"已与《{candidateEbook.Title}》配对");
            };
            Grid.SetColumn(pairBtn, 2);
            g.Children.Add(pairBtn);
        }
        else
        {
            PairCardHost.ContextFlyout = null;
            g.Children.Add(YYIcons.Make("book", 15, ThemeHelper.Brush(p.T2)));
            var mid = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            mid.Children.Add(new TextBlock { Text = "配对电子书", FontSize = 11.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
            mid.Children.Add(new TextBlock { Text = "关联书架电子书后可同步切换阅读", FontSize = 10, Foreground = ThemeHelper.Brush(p.T3) });
            Grid.SetColumn(mid, 1);
            g.Children.Add(mid);

            var pickBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
                Content = shelfEbooks.Length > 0 ? "选电子书…" : "去书架",
                Height = 26,
                MinHeight = 26,
                Padding = new Thickness(10, 0, 10, 0),
                FontSize = 11,
            };
            pickBtn.Click += async (_, _) =>
            {
                if (shelfEbooks.Length > 0) await ShowEbookPairPickerAsync();
                else App.CurrentApp.ShowShelf();
            };
            Grid.SetColumn(pickBtn, 2);
            g.Children.Add(pickBtn);
        }

        PairCardHost.Child = g;
    }

    private void OpenSyncedPairedEbook(string ebookId)
    {
        PausePlayback();
        uint? targetSec = null;
        int? targetSent = null;
        try
        {
            var moment = _library.Core.AudiobookMoment(_bookId, _globalPosSec);
            if (moment.Hit is SyncHit.Sentence sentHit)
            {
                targetSec = sentHit.Section;
                targetSent = (int)sentHit.SentenceValue;
            }
        }
        catch { }

        if (targetSec is null && _recording is not null && _recording.Seconds > 1)
        {
            try
            {
                var detail = _library.GetBookDetail(ebookId);
                if (detail.Sections.Length > 0)
                {
                    double ratio = Math.Clamp(_globalPosSec / _recording.Seconds, 0, 0.999);
                    uint sec = (uint)Math.Clamp((int)Math.Floor(ratio * detail.Sections.Length), 0, detail.Sections.Length - 1);
                    targetSec = sec;
                    targetSent = 0;
                }
            }
            catch { }
        }

        App.CurrentApp.OpenBook(ebookId, section: targetSec, sentence: targetSent);
    }

    private async Task ShowEbookPairPickerAsync()
    {
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);
        var ebooks = _library.GetShelfBooks().Where(b => b.Id != _bookId && b.Format != BookFormat.Audio && b.Format != BookFormat.Pdf).ToArray();

        ContentDialog? dlgRef = null;
        var list = new StackPanel { Spacing = 6, Width = 390 };

        if (ebooks.Length == 0)
        {
            list.Children.Add(new TextBlock
            {
                Text = "书架上还没有可配对的文字电子书（EPUB / TXT / MOBI / AZW3）。先将电子书拖入书架再来配对即可。",
                FontSize = 12,
                Foreground = ThemeHelper.Brush(p.T2),
                TextWrapping = TextWrapping.Wrap,
            });
        }
        else
        {
            foreach (var eb in ebooks)
            {
                var row = new Grid { ColumnSpacing = 10 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var meta = new StackPanel { Spacing = 1 };
                meta.Children.Add(new TextBlock { Text = eb.Title, FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
                meta.Children.Add(new TextBlock { Text = eb.Authors.FirstOrDefault() ?? "电子书", FontSize = 11, Foreground = ThemeHelper.Brush(p.T3) });
                row.Children.Add(meta);
                var selTag = new TextBlock { Text = "配对 ›", FontSize = 11.5, Foreground = ThemeHelper.Brush(tint.Accent), VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(selTag, 1);
                row.Children.Add(selTag);

                var btn = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Padding = new Thickness(12, 8, 12, 8),
                    CornerRadius = new CornerRadius(10),
                    Background = ThemeHelper.Brush(p.Card),
                    BorderBrush = ThemeHelper.Brush(p.Line),
                    BorderThickness = new Thickness(1),
                    UseSystemFocusVisuals = false,
                    Content = row,
                };
                string targetId = eb.Id;
                string targetTitle = eb.Title;
                btn.Click += (_, _) =>
                {
                    try { _library.Core.AudiobookPair(_bookId, targetId); } catch { }
                    dlgRef?.Hide();
                    RenderPairCard();
                    ShowToast($"已与《{targetTitle}》配对");
                };
                list.Children.Add(btn);
            }
        }

        dlgRef = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "选择要配对的电子书",
            Content = new ScrollViewer { MaxHeight = 360, Content = list },
            CloseButtonText = "关闭",
        };
        await dlgRef.ShowAsync();
    }

    private void RecordingSettingsFlyout_Opened(object sender, object e) => RebuildRecordingSettings();

    private void RebuildRecordingSettings()
    {
        RecordingSettingsHost.Children.Clear();
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);

        SkipSettings s;
        try { s = _library.Core.AudiobookSkipSettings(_bookId); }
        catch { s = new SkipSettings(true, false, false, false, false, false, false); }

        double savedSec = 0;
        try { savedSec = _library.Core.AudiobookSaved(_bookId); } catch { }

        RecordingSettingsHost.Children.Add(new TextBlock
        {
            Text = "这本录音",
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = ThemeHelper.Brush(p.T1),
        });

        void SaveSkip(SkipSettings next)
        {
            s = next;
            try { _library.Core.AudiobookSetSkipSettings(_bookId, s); } catch { }
            RebuildRecordingSettings();
        }

        Grid MakeToggleRow(string name, string? detail, bool value, Action<bool> onToggle)
        {
            var g = new Grid { ColumnSpacing = 10, Padding = new Thickness(0, 4, 0, 4) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var sp = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(new TextBlock { Text = name, FontSize = 12, Foreground = ThemeHelper.Brush(p.T1) });
            if (!string.IsNullOrEmpty(detail))
                sp.Children.Add(new TextBlock { Text = detail, FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3) });
            g.Children.Add(sp);
            var sw = new ToggleSwitch { IsOn = value, MinWidth = 0, OnContent = "", OffContent = "", VerticalAlignment = VerticalAlignment.Center };
            sw.Toggled += (_, _) => onToggle(sw.IsOn);
            Grid.SetColumn(sw, 1);
            g.Children.Add(sw);
            return g;
        }

        RecordingSettingsHost.Children.Add(MakeToggleRow("跳过片头片尾", "自动跳过每集开场与结尾口播", s.HeadTail, v => SaveSkip(s with { HeadTail = v })));
        RecordingSettingsHost.Children.Add(MakeToggleRow("压短章间音乐", "章间过场音乐压到 1.5 秒", s.ShortenChapterGaps, v => SaveSkip(s with { ShortenChapterGaps = v })));
        RecordingSettingsHost.Children.Add(MakeToggleRow("人声清楚一点", "压低底噪，人声提一点", s.ClearerVoice, v => SaveSkip(s with { ClearerVoice = v })));
        RecordingSettingsHost.Children.Add(MakeToggleRow(
            "缩短停顿",
            savedSec >= 60 ? $"这本已经省了 {Math.Max(1, (int)Math.Round(savedSec / 60.0))} 分钟" : "长停顿自动压短一些",
            s.ShortenPauses,
            v => SaveSkip(s with { ShortenPauses = v })));
        RecordingSettingsHost.Children.Add(MakeToggleRow("没念的用朗读补上", "与电子书配对时补齐漏读句", s.FillUnread, v => SaveSkip(s with { FillUnread = v })));
        RecordingSettingsHost.Children.Add(MakeToggleRow("逐词跟读", "在配对书页上按词推进下划线", s.WordFollow, v => SaveSkip(s with { WordFollow = v })));

        RecordingSettingsHost.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(0, 2, 0, 2) });

        var alignBtn = new Button
        {
            Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = "对齐检查与微调…",
            FontSize = 11.5,
        };
        alignBtn.Click += (_, _) =>
        {
            RecordingSettingsFlyout.Hide();
            PausePlayback();
            AlignCheckAndPolishService.OpenAlignCheckWindow(_library, _bookId, 0, () => RenderPairCard());
        };
        RecordingSettingsHost.Children.Add(alignBtn);

        ListenPair? curPair = null;
        try { curPair = _library.Core.ListeningPairs().FirstOrDefault(lp => lp.Recording == _bookId); } catch { }
        if (curPair is not null && _library.Core.AudiobookIsTextVersion(curPair.Book))
        {
            string pairedTextId = curPair.Book;
            var polishBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Content = "整理文字版错字…",
                FontSize = 11.5,
            };
            polishBtn.Click += async (_, _) =>
            {
                RecordingSettingsFlyout.Hide();
                await AlignCheckAndPolishService.ShowTextPolishDialogAsync(RootGrid.XamlRoot, _library, pairedTextId);
            };
            RecordingSettingsHost.Children.Add(polishBtn);
        }

        var exportBtn = new Button
        {
            Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = "导出带朗读时间的电子书 (.epub)…",
            FontSize = 11.5,
        };
        exportBtn.Click += async (_, _) =>
        {
            RecordingSettingsFlyout.Hide();
            var picker = new FileSavePicker { SuggestedFileName = _summary.Title };
            picker.FileTypeChoices.Add("EPUB 电子书", [".epub"]);
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var outFile = await picker.PickSaveFileAsync();
            if (outFile is null) return;
            ShowToast("正在打包带音频时间轴的 EPUB…");
            try
            {
                await Task.Run(() => _library.Core.AudiobookExportEpub(_bookId, outFile.Path));
                ShowToast("✓ 已导出有声电子书 (.epub)");
            }
            catch (Exception ex)
            {
                ShowToast($"导出失败（需先配对并对齐电子书）：{ex.Message}");
            }
        };
        RecordingSettingsHost.Children.Add(exportBtn);
    }

    private async Task RenameChapterAsync(int chapterIdx)
    {
        var box = new TextBox { Text = ChapterTitle(chapterIdx), PlaceholderText = "留空恢复默认“第 N 集”" };
        var dlg = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "改名",
            Content = box,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            _summary = _library.Core.RenameAudioChapter(_bookId, (uint)chapterIdx, string.IsNullOrWhiteSpace(box.Text) ? null : box.Text.Trim());
            _recording = _library.Core.AudioRecording(_bookId);
            SyncChapterUi();
            RenderChaptersAndBookmarks();
        }
        catch { }
    }

    // ---------- Transport & Menus ----------

    private void BackToShelf_Click(object sender, RoutedEventArgs e) => App.CurrentApp.ShowShelf();

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_playing) PausePlayback();
        else
        {
            App.CurrentApp.ClaimAudioPlayback(this, () => DispatcherQueue.TryEnqueue(PausePlayback));
            _player.Play();
            _playing = true;
            UpdateTransportUi();
        }
    }

    private async void Rewind15_Click(object sender, RoutedEventArgs e) => await SeekGlobalAsync(_globalPosSec - 15, _playing);
    private async void Forward30_Click(object sender, RoutedEventArgs e) => await SeekGlobalAsync(_globalPosSec + 30, _playing);

    private async void ChapterSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressSlider || _recording is null) return;
        var (chStart, _) = CurrentChapterBounds();
        double target = chStart + e.NewValue;
        if (Math.Abs(target - _globalPosSec) > 1.2)
            await SeekGlobalAsync(target, _playing);
    }

    private void Rate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement anchor) return;
        var menu = new MenuFlyout();
        foreach (var r in SpeedOptions)
        {
            double val = r;
            bool on = Math.Abs(val - _rate) < 0.03;
            var item = new MenuFlyoutItem { Text = (on ? "✓ " : "") + $"{val:0.##}×" };
            item.Click += (_, _) =>
            {
                _rate = val;
                _player.PlaybackSession.PlaybackRate = _rate;
                RateBtn.Content = $"{_rate:0.##}×";
                SyncChapterUi();
            };
            menu.Items.Add(item);
        }
        menu.ShowAt(anchor);
    }

    private void SleepTimer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement anchor) return;
        var menu = new MenuFlyout();

        void AddOpt(int mode, string title)
        {
            var item = new MenuFlyoutItem { Text = (_sleepMode == mode ? "✓ " : "") + title };
            item.Click += (_, _) =>
            {
                _sleepMode = mode;
                if (mode > 0)
                {
                    _sleepUntilUtc = DateTime.UtcNow.AddMinutes(mode);
                }
                else
                {
                    _sleepUntilUtc = null;
                    SleepCountdownText.Text = mode == -1 ? "本集播完自动停止" : "";
                }
                ApplyAppearance();
            };
            menu.Items.Add(item);
        }

        AddOpt(-1, "本集完");
        menu.Items.Add(new MenuFlyoutSeparator());
        foreach (var m in new[] { 15, 30, 45, 60, 90 }) AddOpt(m, $"{m} 分钟");
        if (_sleepMode != 0)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            AddOpt(0, "关闭定时");
        }
        menu.ShowAt(anchor);
    }

    private void AddBookmarkAtCurrentPosition(bool silent = false)
    {
        if (_recording is null || _recording.Chapters.Length == 0) return;
        int ch = CurrentChapterIndex();
        uint ms = (uint)Math.Max(0, Math.Round((_globalPosSec - _recording.Chapters[ch].Start) * 1000.0));
        try
        {
            _library.AddBookmark(_bookId, (uint)ch, ms);
            RenderChaptersAndBookmarks();
            RenderBookmarkTicks();
            if (!silent) ShowToast($"已加书签 · {ChapterTitle(ch)} {FormatTime(ms / 1000.0)}");
        }
        catch { }
    }

    private void ShowToast(string msg)
    {
        ToastText.Text = msg;
        ToastBanner.Visibility = Visibility.Visible;
        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); ToastBanner.Visibility = Visibility.Collapsed; };
        _toastTimer.Start();
    }

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (ctrl && e.Key == VirtualKey.D)
        {
            AddBookmarkAtCurrentPosition();
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case VirtualKey.Space:
                PlayPause_Click(sender, e); e.Handled = true; break;
            case VirtualKey.Left:
                Rewind15_Click(sender, e); e.Handled = true; break;
            case VirtualKey.Right:
                Forward30_Click(sender, e); e.Handled = true; break;
        }
    }

    private void OnSmtcButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (_recording is null) return;
            switch (args.Button)
            {
                case SystemMediaTransportControlsButton.Play: _player.Play(); _playing = true; UpdateTransportUi(); break;
                case SystemMediaTransportControlsButton.Pause: PausePlayback(); break;
                case SystemMediaTransportControlsButton.Next:
                    if (_recording.Chapters.Length > 0)
                    {
                        int next = Math.Min(_recording.Chapters.Length - 1, CurrentChapterIndex() + 1);
                        await SeekGlobalAsync(_recording.Chapters[next].Start, _playing);
                        RenderChaptersAndBookmarks();
                    }
                    break;
                case SystemMediaTransportControlsButton.Previous:
                    if (_recording.Chapters.Length > 0)
                    {
                        int prev = Math.Max(0, CurrentChapterIndex() - 1);
                        await SeekGlobalAsync(_recording.Chapters[prev].Start, _playing);
                        RenderChaptersAndBookmarks();
                    }
                    break;
            }
        });
    }

    private static string FormatTime(double sec)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, sec));
        return ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}" : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _uiTimer.Stop();
        _listenTimeTracker.Stop();
        SaveCurrentProgress();
        try { _player.Pause(); _player.Dispose(); } catch { }
    }
}
