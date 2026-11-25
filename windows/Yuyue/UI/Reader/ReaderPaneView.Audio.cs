using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Yuyue.Core;
using Yuyue.Design;
using Yuyue.Speech;
using Yuyue.UI.Shell;

namespace Yuyue.UI.Reader;

public sealed partial class ReaderPaneView : IMiniPlayerSource
{
    private int _speakingSentence = -1;
    private (uint Section, int Sentence, int DurMs, int ElapsedMs, bool Playing, int[]? Words)? _pendingPairedActive;

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
                    _audio.SetPairedRecording(pair.Recording, rec, map, useRecording: map is not null);
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
            ShowReaderAlert(msg);
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

        _ttsFollowPage = true;
        TtsBarHost.Visibility = Visibility.Visible;
        _audio.SetSectionContext(_currentSection, dramaMode: false);
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
        void Add(string lbl, Action act)
        {
            var mi = new MenuFlyoutItem { Text = L10n.T(lbl) };
            mi.Click += (_, _) => { act(); ApplyPaperAndChrome(); };
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
        Add("取消定时", () => _audio.CancelSleepTimer());
        TtsSleepBtn.Flyout = menu;
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
