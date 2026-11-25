using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Yuyue.Core;
using Yuyue.Design;
using Yuyue.Reader;
using Yuyue.Speech;
using Yuyue.UI.Shell;

namespace Yuyue.UI.Reader;

public sealed partial class ReaderPaneView : IMiniPlayerSource
{
    private int _speakingSentence = -1;
    private (uint Section, int Sentence, int DurMs, int ElapsedMs, bool Playing, int[]? Words)? _pendingPairedActive;
    private (uint Section, int Sentence)? _sleepArmedSpot;

    // IMiniPlayerSource implementation
    string IMiniPlayerSource.SourceId => $"reader:{_bookId}";
    string IMiniPlayerSource.Title => _detail?.Summary.Title ?? "";
    string IMiniPlayerSource.Subtitle => _speakingSentence >= 0 && _speakingSentence < _currentSentences.Count
        ? _currentSentences[_speakingSentence]
        : ResolveCurrentChapterTitle();
    string? IMiniPlayerSource.CoverPath => _detail?.Summary.CoverPath;
    bool IMiniPlayerSource.IsPlaying => _audio?.IsPlaying == true;
    double IMiniPlayerSource.ProgressRatio => _currentSentences.Count > 0
        ? Math.Clamp((double)(_speakingSentence + 1) / _currentSentences.Count, 0.0, 1.0)
        : 0.0;
    double IMiniPlayerSource.PlaybackRate => _audio?.Rate ?? 1.0;

    public event Action? StateChanged;

    private void InitAudioPlayer()
    {
        _audio = new AudioPlayer(_library);
        _audio.SetBookContext(_bookId, _detail.Summary.Language);
        _audio.SetVoice(_library.Prefs.GetVoiceForLanguage(_detail.Summary.Language));
        _audio.SetRate(_library.Prefs.SpeechRate);
        App.CurrentApp.BindAudioClaimOwner(_audio, this);

        _audio.SentenceStarted += OnAudioSentenceStarted;
        _audio.SentenceProgressSynced += OnAudioSentenceProgressSynced;
        _audio.PlayStateChanged += OnAudioPlayStateChanged;
        _audio.SectionFinished += OnAudioSectionFinished;
        _audio.PreviousSectionRequested += OnAudioPreviousSectionRequested;
        _audio.SynthesisError += OnAudioSynthesisError;
        _audio.SleepStateChanged += OnAudioSleepStateChanged;
        _audio.SleepStopped += OnAudioSleepStopped;
        _audio.PairedSentenceActive += OnAudioPairedSentenceActive;

        // Attach paired human audiobook if configured
        try
        {
            var pair = _library.Core.ListeningPairs().FirstOrDefault(x => x.Book == _bookId);
            if (pair is not null)
            {
                var rec = _library.Core.AudioRecording(pair.Recording);
                var map = _library.Core.AudiobookMap(pair.Recording);
                if (rec is not null && rec.Files.Length > 0)
                {
                    _pairedAudioBookId = pair.Recording;
                    _pairedMap = map;
                    _audio.SetPairedRecording(pair.Recording, rec, map, useRecording: map is not null);
                }
            }
        }
        catch { }
    }

    private void OnAudioSentenceStarted(int idx, int durMs, bool playing)
    {
        if (_disposed) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed) return;
            _speakingSentence = idx;
            if (_pager.IsInitialized)
                _ = _pager.Current.SetActiveAsync(idx, durMs, 0, playing);
            if (idx >= 0 && idx < _currentSentences.Count)
                TtsLiveSentenceText.Text = _currentSentences[idx];
            UpdateTtsBarVisuals();
            StateChanged?.Invoke();
        });
    }

    private void OnAudioSentenceProgressSynced(int idx, int durMs, int elapsedMs, bool playing)
    {
        if (_disposed) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed) return;
            _speakingSentence = idx;
            if (_pager.IsInitialized)
                _ = _pager.Current.SetActiveAsync(idx, durMs, elapsedMs, playing);
        });
    }

    private void OnAudioPairedSentenceActive(uint sec, int idx, int durMs, int elapsedMs, bool playing, int[]? words)
    {
        if (_disposed) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed) return;
            if (sec != _currentSection)
            {
                if (!_ttsFollowPage) return;
                _speakingSentence = idx;
                _pendingPairedActive = (sec, idx, durMs, elapsedMs, playing, words);
                _pendingJumpSentence = Math.Max(0, idx);
                _pendingEndOfSection = false;
                LoadSection(sec);
                StateChanged?.Invoke();
                return;
            }
            _pendingPairedActive = null;
            _speakingSentence = idx;
            if (_pager.IsInitialized)
                _ = _pager.Current.SetActiveAsync(idx, durMs, elapsedMs, playing, words);
            if (idx >= 0 && idx < _currentSentences.Count)
                TtsLiveSentenceText.Text = _currentSentences[idx];
            UpdateTtsBarVisuals();
            StateChanged?.Invoke();
        });
    }

    private void OnAudioPlayStateChanged(bool playing)
    {
        if (_disposed) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed) return;
            if (playing)
            {
                App.CurrentApp.ClaimAudioPlayback(this, () => _audio.Pause());
                App.CurrentApp.SetActiveMiniPlayerSource(this);
            }
            _library.SystemIntegration.SetAudioPlaying(this, playing, _detail.Summary.Title, ToggleListenPlayback);
            if (_pager.IsInitialized && _speakingSentence >= 0)
                _ = _pager.Current.SetPlayingAsync(playing);
            ApplyPaperAndChrome();
            UpdateTtsBarVisuals();
            StateChanged?.Invoke();
        });
    }

    private void OnAudioSectionFinished()
    {
        if (_disposed) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed) return;
            if (_library.NextSection(_bookId, _currentSection, speech: true) is uint next)
            {
                _autoListenOnReady = true;
                _ttsFollowPage = true;
                _pendingEndOfSection = false;
                LoadSection(next);
            }
            else
            {
                ShowReaderAlert("本书已朗读完毕");
                TtsBarHost.Visibility = Visibility.Collapsed;
            }
        });
    }

    private void OnAudioPreviousSectionRequested()
    {
        if (_disposed) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed) return;
            if (_library.PreviousSection(_bookId, _currentSection, speech: true) is uint prev)
            {
                _autoListenOnReady = true;
                _ttsFollowPage = true;
                _pendingEndOfSection = false;
                LoadSection(prev);
            }
        });
    }

    private void OnAudioSynthesisError(string err)
    {
        if (_disposed) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed) return;
            ShowReaderAlert($"朗读遇到问题：{err}");
        });
    }

    private void OnAudioSleepStateChanged()
    {
        if (_disposed) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed) return;
            ApplyPaperAndChrome();
        });
    }

    private void OnAudioSleepStopped(string msg)
    {
        if (_disposed) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed) return;
            var armed = _sleepArmedSpot ?? (_currentSection, CurrentReadingSentence());
            _sleepArmedSpot = null;
            ReaderRecapAndSleepHelper.RememberSleepStop(_library, _bookId, armed.Section, armed.Sentence);
            try { _library.AddBookmark(_bookId, _currentSection, (uint)Math.Max(0, CurrentReadingSentence())); } catch { }
            ShowReaderAlert(msg);
            ApplyPaperAndChrome();
        });
    }

    public void TriggerListen() => StartListenFromCurrentPage();

    private void StartListenFromCurrentPage()
    {
        int startIdx = CurrentReadingSentence();
        StartListenFromSentence(startIdx);
    }

    private void StartListenFromSentence(int startIdx)
    {
        if (_currentSentences.Count == 0)
        {
            try { _currentSentences = _library.GetSpokenSentences(_bookId, _currentSection); }
            catch { _currentSentences = []; }
        }
        if (_currentSentences.Count == 0)
        {
            ShowReaderAlert("本节没有可朗读的正文内容");
            return;
        }

        SleepReturnHost.Visibility = Visibility.Collapsed;
        ReaderRecapAndSleepHelper.ClearSleepReturn(_library, _bookId);

        _ttsFollowPage = true;
        TtsBarHost.Visibility = Visibility.Visible;
        _audio.SetSectionContext(_currentSection, dramaMode: _dramaModeOn);
        _audio.SetMetadata(_detail.Summary.Title, ResolveCurrentChapterTitle(), _detail.Summary.CoverPath);
        _audio.Start(_currentSentences, Math.Clamp(startIdx, 0, _currentSentences.Count - 1));
        App.CurrentApp.SetActiveMiniPlayerSource(this);
        UpdateTtsBarVisuals();
    }

    private void ToggleListenPlayback()
    {
        if (TtsBarHost.Visibility != Visibility.Visible)
        {
            StartListenFromCurrentPage();
            return;
        }
        if (_audio.IsPlaying) _audio.Pause();
        else if (_speakingSentence >= 0)
        {
            SleepReturnHost.Visibility = Visibility.Collapsed;
            ReaderRecapAndSleepHelper.ClearSleepReturn(_library, _bookId);
            _ttsFollowPage = true;
            _audio.Resume();
        }
        else StartListenFromCurrentPage();
    }

    private void UpdateTtsBarVisuals()
    {
        bool playing = _audio?.IsPlaying == true;
        TtsPlayPauseBtn.Content = YYIcons.Make(playing ? "pause" : "play", 12, ThemeHelper.Brush(Colors.White), filled: true);
        TtsVoiceLabel.Text = _audio?.VoiceName ?? L10n.T("朗读");
        TtsRateBtn.Content = $"{(_audio?.Rate ?? 1.0):0.##}×";
        if (string.IsNullOrWhiteSpace(TtsLiveSentenceText.Text))
        {
            string ch = ResolveCurrentChapterTitle();
            TtsLiveSentenceText.Text = string.IsNullOrWhiteSpace(ch)
                ? L10n.T("按空格开始或暂停朗读 · 支持边听边翻页")
                : $"{ch} · {L10n.T("按空格开始朗读")}";
        }
    }

    private void BuildSleepTimerFlyout()
    {
        var menu = new MenuFlyout();
        void Add(string lbl, Action act, bool armsSleep = true)
        {
            var mi = new MenuFlyoutItem { Text = L10n.T(lbl) };
            mi.Click += (_, _) =>
            {
                if (armsSleep) _sleepArmedSpot = (_currentSection, CurrentReadingSentence());
                else _sleepArmedSpot = null;
                act();
                ApplyPaperAndChrome();
            };
            menu.Items.Add(mi);
        }
        Add("15 分钟后停止", () => _audio.StartSleepTimer(15));
        Add("30 分钟后停止", () => _audio.StartSleepTimer(30));
        Add("45 分钟后停止", () => _audio.StartSleepTimer(45));
        Add("60 分钟后停止", () => _audio.StartSleepTimer(60));
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("播完本章停止", () => _audio.SetSleepEndOfChapter());
        Add("播完本句停止", () => _audio.SetSleepAfterSentence());
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("取消定时", () => _audio.CancelSleepTimer(), armsSleep: false);
        TtsSleepBtn.Flyout = menu;
    }

    public Task ShowVoicePickerSheetAsync(string title, string? currentVoiceId, Action<string> onPicked, string? roleName = null)
    {
        if (RootGrid.XamlRoot is not { } xr) return Task.CompletedTask;
        return ShowVoicePickerDialogAsync(xr, _library, title, currentVoiceId, onPicked, bookId: _bookId, roleName: roleName, darkOverride: CurrentPaperChrome?.IsDark);
    }

    public static async Task ShowVoicePickerDialogAsync(
        XamlRoot xamlRoot,
        LibraryService library,
        string title,
        string? currentVoiceId,
        Action<string> onPicked,
        string? bookId = null,
        string? roleName = null,
        bool? darkOverride = null)
    {
        bool dark = darkOverride ?? ThemeHelper.IsDark(library.Prefs.ToElementTheme());
        var p = ThemeHelper.Palette(dark);
        var tc = ThemeHelper.TintColors(library.Prefs.Tint, dark);
        ContentDialog? dlgRef = null;

        var allVoices = AudioPlayer.GetAllVoiceOptions(library, bookId);
        string normCurrent = DramaSynthService.NormalizeVoiceId(currentVoiceId) ?? (currentVoiceId ?? "");

        var searchBox = new TextBox
        {
            Style = (Style)Application.Current.Resources["YYSingleLineTextBoxStyle"],
            PlaceholderText = L10n.T("筛选音色名称或语言…"),
            Height = 32,
            MinHeight = 32,
        };

        var listStack = new StackPanel { Spacing = 6, Padding = new Thickness(0, 2, 4, 8) };

        void Rebuild(string q)
        {
            listStack.Children.Clear();
            string query = q.Trim();
            var filtered = string.IsNullOrEmpty(query)
                ? allVoices
                : allVoices.Where(v =>
                    v.Label.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || v.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || (v.Language?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)).ToList();

            if (filtered.Count == 0)
            {
                listStack.Children.Add(new TextBlock
                {
                    Text = L10n.T("没有匹配的音色"),
                    FontSize = 12,
                    Foreground = ThemeHelper.Brush(p.T3),
                    Margin = new Thickness(4, 14, 4, 14),
                });
                return;
            }

            (SpeechVoiceKind Kind, string Heading)[] groups =
            [
                (SpeechVoiceKind.Kokoro, "Kokoro 离线高保真音色"),
                (SpeechVoiceKind.ClonedCard, "我的克隆音色"),
                (SpeechVoiceKind.BookCard, "本书定制角色卡"),
                (SpeechVoiceKind.BuiltinCard, "内置神经音色卡"),
                (SpeechVoiceKind.Cloud, "云端与本地声源"),
                (SpeechVoiceKind.System, "Windows 系统与 Edge 音色"),
            ];

            foreach (var (kind, heading) in groups)
            {
                var items = filtered.Where(v => v.Kind == kind).ToList();
                if (items.Count == 0) continue;

                listStack.Children.Add(new TextBlock
                {
                    Text = L10n.T(heading),
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = ThemeHelper.Brush(p.T3),
                    Margin = new Thickness(2, 6, 0, 2),
                });

                foreach (var v in items)
                {
                    var vCap = v;
                    string normV = DramaSynthService.NormalizeVoiceId(vCap.Id) ?? vCap.Id;
                    bool isSel = !string.IsNullOrEmpty(normCurrent)
                        && (string.Equals(normV, normCurrent, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(vCap.Id, currentVoiceId, StringComparison.OrdinalIgnoreCase));

                    var row = new Grid { ColumnSpacing = 8 };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var meta = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
                    meta.Children.Add(L10n.Skip(new TextBlock
                    {
                        Text = vCap.Label,
                        FontSize = 12.5,
                        FontWeight = isSel ? FontWeights.SemiBold : FontWeights.Normal,
                        Foreground = ThemeHelper.Brush(isSel ? tc.Accent : p.T1),
                        MaxLines = 1,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    }));
                    string sub = string.Join(" · ", new[] { vCap.Language, vCap.Gender }.Where(s => !string.IsNullOrWhiteSpace(s)));
                    if (!string.IsNullOrEmpty(sub))
                    {
                        meta.Children.Add(new TextBlock
                        {
                            Text = sub,
                            FontSize = 10.5,
                            Foreground = ThemeHelper.Brush(p.T3),
                        });
                    }
                    Grid.SetColumn(meta, 0);
                    row.Children.Add(meta);

                    var previewBtn = new Button
                    {
                        Style = (Style)Application.Current.Resources["YYFluentButtonStyle"],
                        Height = 26,
                        MinHeight = 26,
                        Padding = new Thickness(9, 0, 9, 0),
                        FontSize = 11,
                        Content = L10n.T("试听"),
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    previewBtn.Click += async (_, _) =>
                    {
                        string sample = (vCap.Language ?? "").StartsWith("en", StringComparison.OrdinalIgnoreCase)
                            ? "Reading is a quiet conversation across time."
                            : "读书如晤对古人，一灯如豆，满室生春。";
                        try { await AudioPlayer.PreviewVoiceAsync(library, vCap.Id, library.Prefs.SpeechRate, sample); }
                        catch { }
                    };
                    Grid.SetColumn(previewBtn, 1);
                    row.Children.Add(previewBtn);

                    var pickBtn = new Button
                    {
                        Style = (Style)Application.Current.Resources[isSel ? "YYFluentAccentButtonStyle" : "YYFluentButtonStyle"],
                        Height = 26,
                        MinHeight = 26,
                        Padding = new Thickness(10, 0, 10, 0),
                        FontSize = 11,
                        Content = L10n.T(isSel ? "当前" : "选用"),
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    pickBtn.Click += (_, _) =>
                    {
                        AudioPlayer.StopActivePreview();
                        onPicked(vCap.Id);
                        dlgRef?.Hide();
                    };
                    Grid.SetColumn(pickBtn, 2);
                    row.Children.Add(pickBtn);

                    listStack.Children.Add(new Border
                    {
                        CornerRadius = new CornerRadius(6),
                        Background = ThemeHelper.Brush(isSel ? tc.Soft : p.Card),
                        BorderBrush = ThemeHelper.Brush(isSel ? tc.Edge : p.Line),
                        BorderThickness = new Thickness(1),
                        Padding = new Thickness(10, 7, 10, 7),
                        Child = row,
                    });
                }
            }
        }

        searchBox.TextChanged += (_, _) => Rebuild(searchBox.Text ?? "");
        Rebuild("");

        var root = new StackPanel
        {
            Spacing = 10,
            Padding = new Thickness(20, 16, 20, 16),
        };
        root.Children.Add(YYChrome.MakeDialogHeader(
            title,
            string.IsNullOrWhiteSpace(roleName) ? L10n.T($"共 {allVoices.Count} 个可用音色，支持离线 / 云端 / 克隆试听") : L10n.T($"为「{roleName}」挑选专属声音"),
            () => { AudioPlayer.StopActivePreview(); dlgRef?.Hide(); },
            dark,
            titleSize: 16));
        root.Children.Add(searchBox);
        root.Children.Add(new ScrollViewer { MaxHeight = 420, Content = listStack });

        dlgRef = YYChrome.CreateFluentDialog(xamlRoot, dark, 520, root, tint: library.Prefs.Tint);
        dlgRef.Closed += (_, _) => AudioPlayer.StopActivePreview();
        await dlgRef.ShowSafeAsync(library.Prefs.Tint);
    }

    private void OpenMiniPlayer()
    {
        App.CurrentApp.ShowMiniPlayer(this);
    }

    // Event handlers
    private void ListenBtn_Click(object sender, RoutedEventArgs e) => ToggleListenPlayback();
    private void TtsPlayPause_Click(object sender, RoutedEventArgs e) => ToggleListenPlayback();
    private void TtsPrev_Click(object sender, RoutedEventArgs e) { _ttsFollowPage = true; _audio.Previous(); }
    private void TtsNext_Click(object sender, RoutedEventArgs e) { _ttsFollowPage = true; _audio.Next(); }

    private void TtsRate_Click(object sender, RoutedEventArgs e)
    {
        double[] rates = [0.8, 1.0, 1.15, 1.25, 1.5, 1.75, 2.0];
        int idx = Array.FindIndex(rates, r => Math.Abs(r - _audio.Rate) < 0.05);
        double next = rates[(idx + 1) % rates.Length];
        _audio.SetRate(next);
        _library.Prefs.SpeechRate = next;
        _library.SavePrefs();
        UpdateTtsBarVisuals();
        StateChanged?.Invoke();
    }

    private void TtsPureListen_Click(object sender, RoutedEventArgs e)
    {
        ListenOnlyTitle.Text = $"{_detail.Summary.Title}\n{ResolveCurrentChapterTitle()}";
        ListenOnlyCover.Visibility = ListenOnlyCover.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        ApplyPaperAndChrome();
    }

    private void TtsOpenMiniPlayer_Click(object sender, RoutedEventArgs e) => OpenMiniPlayer();

    private void TtsStop_Click(object sender, RoutedEventArgs e)
    {
        _audio.Pause();
        TtsBarHost.Visibility = Visibility.Collapsed;
        ListenOnlyCover.Visibility = Visibility.Collapsed;
        if (_pager.IsInitialized) _ = _pager.Current.SetActiveAsync(null);
        ApplyPaperAndChrome();
        StateChanged?.Invoke();
    }

    // IMiniPlayerSource methods
    void IMiniPlayerSource.TogglePlayPause() { if (!_disposed) ToggleListenPlayback(); }
    void IMiniPlayerSource.SkipPrevious() { if (_disposed) return; _ttsFollowPage = true; _audio.Previous(); }
    void IMiniPlayerSource.SkipNext() { if (_disposed) return; _ttsFollowPage = true; _audio.Next(); }
    void IMiniPlayerSource.CycleRate() { if (!_disposed) TtsRate_Click(this, new RoutedEventArgs()); }
    void IMiniPlayerSource.JumpToSource()
    {
        if (_disposed) return;
        var tab = _ownerTab ?? _ownerWindow.FindTabByBookId(_bookId);
        if (tab is not null)
        {
            App.CurrentApp.RestoreAndSelectTab(_ownerWindow, tab);
        }
        else
        {
            App.CurrentApp.ShowShelf();
        }
    }

    private void DisposeAudioPlayer()
    {
        try
        {
            if (_audio is not null)
            {
                _audio.SentenceStarted -= OnAudioSentenceStarted;
                _audio.SentenceProgressSynced -= OnAudioSentenceProgressSynced;
                _audio.PlayStateChanged -= OnAudioPlayStateChanged;
                _audio.SectionFinished -= OnAudioSectionFinished;
                _audio.PreviousSectionRequested -= OnAudioPreviousSectionRequested;
                _audio.SynthesisError -= OnAudioSynthesisError;
                _audio.SleepStateChanged -= OnAudioSleepStateChanged;
                _audio.SleepStopped -= OnAudioSleepStopped;
                _audio.PairedSentenceActive -= OnAudioPairedSentenceActive;
                _audio.Pause();
                _audio.Dispose();
            }
        }
        catch { }
        try { App.CurrentApp.ClearActiveMiniPlayerSource(this); } catch { }
        try { App.CurrentApp.ReleaseAudioClaim(this); } catch { }
        try { _library.SystemIntegration.ClearAudioSource(this); } catch { }
    }
}
