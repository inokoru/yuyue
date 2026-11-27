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
    private bool _sleepStopAtPageEnd;
    private int _sleepEndPageTarget = -1;

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
        BuildRateFlyout();

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
            if (_sleepStopAtPageEnd && _sleepEndPageTarget >= 0 && _layout is not null && _layout.PageOf(idx) > _sleepEndPageTarget)
            {
                _sleepStopAtPageEnd = false;
                _sleepEndPageTarget = -1;
                var armed = _sleepArmedSpot ?? (_currentSection, CurrentReadingSentence());
                _sleepArmedSpot = null;
                _audio.Pause();
                ReaderRecapAndSleepHelper.RememberSleepStop(_library, _bookId, armed.Section, armed.Sentence);
                try { _library.AddBookmark(_bookId, _currentSection, (uint)Math.Max(0, CurrentReadingSentence())); } catch { }
                ShowReaderAlert("本页读完已自动暂停");
                ApplyPaperAndChrome();
                return;
            }
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
            if (_sleepStopAtPageEnd)
            {
                _sleepStopAtPageEnd = false;
                _sleepEndPageTarget = -1;
                var armed = _sleepArmedSpot ?? (_currentSection, CurrentReadingSentence());
                _sleepArmedSpot = null;
                ReaderRecapAndSleepHelper.RememberSleepStop(_library, _bookId, armed.Section, armed.Sentence);
                try { _library.AddBookmark(_bookId, _currentSection, (uint)Math.Max(0, CurrentReadingSentence())); } catch { }
                ShowReaderAlert("本页读完已自动暂停");
                ApplyPaperAndChrome();
                return;
            }
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

        bool pDark = CurrentPaperChrome?.IsDark ?? ThemeHelper.IsDark(_library.Prefs.ToElementTheme());
        var accent = ThemeHelper.Brush(ThemeHelper.TintColors(_library.Prefs.Tint, pDark).Accent);
        var fg2 = ThemeHelper.Brush(CurrentPaperChrome?.Ink2 ?? ThemeHelper.Palette(pDark).T2);
        string? sleepTag = FormatActiveSleepTag();
        var sleepIcon = YYIcons.Make("moon", 13.5, sleepTag is not null ? accent : fg2);
        if (sleepTag is not null)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(sleepIcon);
            sp.Children.Add(L10n.Skip(new TextBlock
            {
                Text = sleepTag,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = accent,
                VerticalAlignment = VerticalAlignment.Center,
            }));
            TtsSleepBtn.Content = sp;
        }
        else
        {
            TtsSleepBtn.Content = sleepIcon;
        }

        if (string.IsNullOrWhiteSpace(TtsLiveSentenceText.Text))
        {
            string ch = ResolveCurrentChapterTitle();
            TtsLiveSentenceText.Text = string.IsNullOrWhiteSpace(ch)
                ? L10n.T("按空格开始或暂停朗读 · 支持边听边翻页")
                : $"{ch} · {L10n.T("按空格开始朗读")}";
        }
    }

    private string? FormatActiveSleepTag()
    {
        if (_sleepStopAtPageEnd) return L10n.T("本页完");
        if (_audio is null) return null;
        return _audio.SleepMode switch
        {
            1 => FormatCountdown(_audio.SleepSecondsRemaining),
            2 => L10n.T("本章完"),
            3 => L10n.T("本句完"),
            _ => null,
        };
    }

    private static string FormatCountdown(double seconds)
    {
        int total = Math.Max(0, (int)Math.Ceiling(seconds));
        int m = total / 60;
        int s = total % 60;
        return $"{m:D2}:{s:D2}";
    }

    private void BuildRateFlyout()
    {
        var menu = new MenuFlyout();
        double[] rates = [0.8, 1.0, 1.15, 1.25, 1.5, 1.75, 2.0];
        double cur = _audio?.Rate ?? _library.Prefs.SpeechRate;
        foreach (double r in rates)
        {
            double rCap = r;
            var mi = new ToggleMenuFlyoutItem
            {
                Text = $"{rCap:0.##}×",
                IsChecked = Math.Abs(rCap - cur) < 0.04,
            };
            mi.Click += (_, _) => ApplyTtsRate(rCap);
            menu.Items.Add(mi);
        }
        TtsRateBtn.Flyout = menu;
    }

    private void ApplyTtsRate(double rate)
    {
        _audio.SetRate(rate);
        _library.Prefs.SpeechRate = rate;
        _library.SavePrefs();
        BuildRateFlyout();
        UpdateTtsBarVisuals();
        StateChanged?.Invoke();
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
        Add("本句读完", () => { _sleepStopAtPageEnd = false; _sleepEndPageTarget = -1; _audio.SetSleepAfterSentence(); });
        Add("本页读完", () =>
        {
            _audio.CancelSleepTimer();
            int curSent = _speakingSentence >= 0 ? _speakingSentence : CurrentReadingSentence();
            _sleepEndPageTarget = _layout is not null ? _layout.PageOf(Math.Max(0, curSent)) : _currentPage;
            _sleepStopAtPageEnd = true;
            ShowReaderAlert("已设定：本页读完自动暂停");
        });
        Add("本章结束", () => { _sleepStopAtPageEnd = false; _sleepEndPageTarget = -1; _audio.SetSleepEndOfChapter(); });
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("15 分钟", () => { _sleepStopAtPageEnd = false; _sleepEndPageTarget = -1; _audio.StartSleepTimer(15); });
        Add("30 分钟", () => { _sleepStopAtPageEnd = false; _sleepEndPageTarget = -1; _audio.StartSleepTimer(30); });
        Add("60 分钟", () => { _sleepStopAtPageEnd = false; _sleepEndPageTarget = -1; _audio.StartSleepTimer(60); });
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("关", () => { _sleepStopAtPageEnd = false; _sleepEndPageTarget = -1; _audio.CancelSleepTimer(); }, armsSleep: false);
        TtsSleepBtn.Flyout = menu;
    }

    private async void TtsVoice_Click(object sender, RoutedEventArgs e)
    {
        string langTag = (_detail?.Summary.Language ?? "").StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "en" : "zh";
        string curVoice = _library.Prefs.GetVoiceForLanguage(_detail?.Summary.Language);
        await ShowVoicePickerSheetAsync(
            L10n.T("选择朗读声音"),
            curVoice,
            vid =>
            {
                string norm = DramaSynthService.NormalizeVoiceId(vid) ?? vid;
                _library.Prefs.SetVoiceForLanguage(_detail?.Summary.Language, norm);
                _library.SavePrefs();
                bool wasPlaying = _audio.IsPlaying;
                int resumeIdx = _speakingSentence >= 0 ? _speakingSentence : CurrentReadingSentence();
                _audio.SetVoice(norm);
                App.CurrentApp.BroadcastVoiceChanged();
                UpdateTtsBarVisuals();
                if (wasPlaying && _currentSentences.Count > 0)
                {
                    _audio.Start(_currentSentences, Math.Clamp(resumeIdx, 0, _currentSentences.Count - 1));
                }
            },
            languageFilter: langTag);
    }

    public Task ShowVoicePickerSheetAsync(
        string title,
        string? currentVoiceId,
        Action<string> onPicked,
        string? roleName = null,
        string? languageFilter = null,
        string? initialSourceFilter = null)
    {
        if (RootGrid.XamlRoot is not { } xr) return Task.CompletedTask;
        return ShowVoicePickerDialogAsync(
            xr,
            _library,
            title,
            currentVoiceId,
            onPicked,
            bookId: _bookId,
            roleName: roleName,
            darkOverride: CurrentPaperChrome?.IsDark,
            languageFilter: languageFilter,
            initialSourceFilter: initialSourceFilter);
    }

    internal static readonly (string Text, string Source)[] AuditionQuotesZh =
    [
        ("江上之清风，与山间之明月，耳得之而为声，目遇之而成色。", "苏轼《赤壁赋》"),
        ("月光如流水一般，静静地泻在这一片叶子和花上。", "朱自清《荷塘月色》"),
        ("行到水穷处，坐看云起时。偶然值林叟，谈笑无还期。", "王维《终南别业》"),
        ("一个人只拥有此生此世是不够的，他还应该拥有诗意的世界。", "王小波《万寿寺》"),
        ("太阳，它每时每刻都是夕阳也都是旭日。", "史铁生《我与地坛》"),
        ("花开了，就像睡醒了似的。鸟飞了，就像在天上逛似的。", "萧红《呼兰河传》"),
        ("雾凇沆砀，天与云与山与水，上下一白。", "张岱《湖心亭看雪》"),
    ];

    internal static readonly (string Text, string Source)[] AuditionQuotesEn =
    [
        ("Reading is a quiet conversation across time, carried by a single human voice.", "Yuyue"),
        ("I declare after all there is no enjoyment like reading!", "Jane Austen"),
        ("So we beat on, boats against the current, borne back ceaselessly into the past.", "F. Scott Fitzgerald"),
        ("We are all in the gutter, but some of us are looking at the stars.", "Oscar Wilde"),
        ("Only that day dawns to which we are awake. There is more day to dawn.", "Henry David Thoreau"),
        ("Hope is the thing with feathers that perches in the soul.", "Emily Dickinson"),
    ];

    internal static string PickDailyAuditionQuote(bool english, int offset = 0)
    {
        var pool = english ? AuditionQuotesEn : AuditionQuotesZh;
        int day = DateTime.Now.DayOfYear + offset;
        int idx = ((day % pool.Length) + pool.Length) % pool.Length;
        return pool[idx].Text;
    }

    private static string ResolveVoiceSourceFilterKey(SpeechVoiceOption v)
    {
        if (v.Kind is SpeechVoiceKind.Kokoro or SpeechVoiceKind.BuiltinCard or SpeechVoiceKind.BookCard) return "kokoro";
        if (v.Kind == SpeechVoiceKind.ClonedCard) return "cloned";
        if (v.Kind == SpeechVoiceKind.Cloud && CloudVoiceService.TryParseFullId(v.Id, out var cp, out _))
            return $"cloud.{CloudVoiceEntry.ProviderKey(cp)}";
        if (v.Kind == SpeechVoiceKind.System) return "system";
        return "other";
    }

    private static string? ResolveVoiceBrandKey(SpeechVoiceOption v)
    {
        if (v.Kind == SpeechVoiceKind.Cloud && CloudVoiceService.TryParseFullId(v.Id, out var cp, out _))
            return CloudVoiceEntry.ProviderKey(cp);
        return null;
    }

    private static bool MatchesLanguageTag(SpeechVoiceOption v, string langFilter)
    {
        if (string.IsNullOrEmpty(langFilter) || langFilter == "all") return true;
        if (string.IsNullOrWhiteSpace(v.Language)) return true; // multilingual cloud voices speak both zh & en
        string l = v.Language.ToLowerInvariant();
        if (langFilter == "zh") return l.StartsWith("zh") || l.Contains("cn") || l.Contains("tw") || l.Contains("hk");
        if (langFilter == "en") return l.StartsWith("en");
        return true;
    }

    public static async Task ShowVoicePickerDialogAsync(
        XamlRoot xamlRoot,
        LibraryService library,
        string title,
        string? currentVoiceId,
        Action<string> onPicked,
        string? bookId = null,
        string? roleName = null,
        bool? darkOverride = null,
        string? languageFilter = null,
        string? initialSourceFilter = null)
    {
        bool dark = darkOverride ?? ThemeHelper.IsDark(library.Prefs.ToElementTheme());
        var p = ThemeHelper.Palette(dark);
        var tc = ThemeHelper.TintColors(library.Prefs.Tint, dark);
        ContentDialog? dlgRef = null;
        bool dialogOpen = true;

        var allVoices = new List<SpeechVoiceOption>(AudioPlayer.GetAllVoiceOptions(library, bookId));
        string normCurrent = DramaSynthService.NormalizeVoiceId(currentVoiceId) ?? (currentVoiceId ?? "");

        string activeLang = languageFilter is "zh" or "en" ? languageFilter : "all";
        string activeSource = string.IsNullOrWhiteSpace(initialSourceFilter) ? "all" : initialSourceFilter;
        int quoteOffset = 0;
        bool userEditedSample = false;
        string? previewingVoiceId = null;
        int previewSeq = 0;

        var sourceTabs = new List<(string Key, string Label, string? BrandId)>();
        var sourceTrack = new Border
        {
            CornerRadius = new CornerRadius(6),
            Background = ThemeHelper.Brush(p.Inset),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(2),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var sourceTabsHost = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        sourceTrack.Child = sourceTabsHost;
        var sourceScroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = sourceTrack,
        };

        var searchBox = YYChrome.MakeSearchBox("搜声音、来源、音色气质（温暖、沉稳、男声、女声…）");
        var listStack = new StackPanel { Spacing = 6, Padding = new Thickness(0, 2, 10, 8) };

        // Calm warm-stationery audition quote strip (distinct from search TextBox below)
        var sampleBox = new TextBox
        {
            Text = PickDailyAuditionQuote(activeLang == "en", quoteOffset),
            PlaceholderText = L10n.T("输入任意一句话，点下方音色即可试听对比…"),
            FontFamily = new FontFamily(YYFontFamily.Serif.WindowsFamily()),
            FontSize = 12.5,
            Foreground = ThemeHelper.Brush(p.T1),
            Padding = new Thickness(2, 5, 2, 5),
            MinHeight = 28,
            VerticalAlignment = VerticalAlignment.Center,
        };
        YYChrome.StripTextBoxChrome(sampleBox);
        sampleBox.TextChanged += (_, _) =>
        {
            if (sampleBox.FocusState != FocusState.Unfocused)
                userEditedSample = !string.IsNullOrWhiteSpace(sampleBox.Text);
        };

        string ResolveSampleText(SpeechVoiceOption v)
        {
            string typed = (sampleBox.Text ?? "").Trim();
            if (!string.IsNullOrEmpty(typed)) return typed;
            bool isEn = (v.Language ?? "").StartsWith("en", StringComparison.OrdinalIgnoreCase) || activeLang == "en";
            return PickDailyAuditionQuote(isEn, quoteOffset);
        }

        Action? rebuildListRef = null;

        void RebuildSourceTabsData()
        {
            sourceTabs.Clear();
            sourceTabs.Add(("all", L10n.T("全部"), null));
            if (allVoices.Any(v => ResolveVoiceSourceFilterKey(v) == "kokoro"))
                sourceTabs.Add(("kokoro", L10n.T("离线语音"), null));
            if (allVoices.Any(v => ResolveVoiceSourceFilterKey(v) == "cloned"))
                sourceTabs.Add(("cloned", L10n.T("我的克隆"), null));
            foreach (var prov in CloudVoiceService.AllProviders)
            {
                string sKey = $"cloud.{CloudVoiceEntry.ProviderKey(prov)}";
                if (allVoices.Any(v => ResolveVoiceSourceFilterKey(v) == sKey))
                    sourceTabs.Add((sKey, CloudVoiceService.ProviderDisplayName(prov, library.Prefs), CloudVoiceEntry.ProviderKey(prov)));
            }
            if (allVoices.Any(v => v.Kind == SpeechVoiceKind.System))
                sourceTabs.Add(("system", L10n.T("系统自带"), null));
            if (!sourceTabs.Any(t => t.Key == activeSource))
                activeSource = "all";
        }

        async Task TogglePreviewAsync(SpeechVoiceOption v)
        {
            if (string.Equals(previewingVoiceId, v.Id, StringComparison.OrdinalIgnoreCase))
            {
                previewSeq++;
                previewingVoiceId = null;
                AudioPlayer.StopActivePreview();
                rebuildListRef?.Invoke();
                return;
            }

            int mySeq = ++previewSeq;
            previewingVoiceId = v.Id;
            rebuildListRef?.Invoke();
            try
            {
                await AudioPlayer.PreviewVoiceAsync(library, v.Id, library.Prefs.SpeechRate, ResolveSampleText(v));
            }
            catch { }
            finally
            {
                if (dialogOpen && previewSeq == mySeq && string.Equals(previewingVoiceId, v.Id, StringComparison.OrdinalIgnoreCase))
                {
                    previewingVoiceId = null;
                    rebuildListRef?.Invoke();
                }
            }
        }

        void RefreshSourcePills()
        {
            // Only show the segmented source switcher when >= 2 actual sources exist (no redundant "全部 | 系统自带")
            if (sourceTabs.Count <= 2)
            {
                sourceScroller.Visibility = Visibility.Collapsed;
                return;
            }
            sourceScroller.Visibility = Visibility.Visible;
            sourceTabsHost.Children.Clear();

            Windows.UI.Color activeBorderColor = dark ? Windows.UI.Color.FromArgb(26, 255, 255, 255) : Windows.UI.Color.FromArgb(20, 42, 43, 46);
            Windows.UI.Color inactiveHoverColor = dark ? Windows.UI.Color.FromArgb(18, 255, 255, 255) : Windows.UI.Color.FromArgb(12, 42, 43, 46);

            foreach (var (sKey, sLabel, brandId) in sourceTabs)
            {
                string keyCap = sKey;
                bool sel = activeSource == keyCap;
                var bgBrush = new SolidColorBrush(sel ? p.Card : Colors.Transparent);
                var hoverBg = new SolidColorBrush(sel ? p.Card : inactiveHoverColor);

                var tabInner = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
                if (!string.IsNullOrEmpty(brandId) && ThemeHelper.CreateBrandLogoImage(brandId, dark, 14) is { } img)
                    tabInner.Children.Add(img);
                tabInner.Children.Add(L10n.Skip(new TextBlock
                {
                    Text = sLabel,
                    FontSize = 11.5,
                    FontWeight = sel ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = ThemeHelper.Brush(sel ? p.T1 : p.T2),
                    VerticalAlignment = VerticalAlignment.Center,
                }));

                var tabBtn = new Button
                {
                    Height = 26,
                    MinHeight = 26,
                    Padding = new Thickness(10, 0, 10, 0),
                    CornerRadius = new CornerRadius(4),
                    Background = bgBrush,
                    BorderBrush = sel ? ThemeHelper.Brush(activeBorderColor) : null,
                    BorderThickness = new Thickness(sel ? 1 : 0),
                    UseSystemFocusVisuals = false,
                    Content = tabInner,
                };
                tabBtn.Resources["ButtonBackground"] = bgBrush;
                tabBtn.Resources["ButtonBackgroundPointerOver"] = hoverBg;
                tabBtn.Resources["ButtonBackgroundPressed"] = hoverBg;
                tabBtn.Click += (_, _) =>
                {
                    activeSource = keyCap;
                    RefreshSourcePills();
                    rebuildListRef?.Invoke();
                };
                sourceTabsHost.Children.Add(tabBtn);
            }
        }

        Border MakeVoiceRowCard(SpeechVoiceOption v, bool isPinnedCurrent)
        {
            var vCap = v;
            string normV = DramaSynthService.NormalizeVoiceId(vCap.Id) ?? vCap.Id;
            bool isSel = !string.IsNullOrEmpty(normCurrent)
                && (string.Equals(normV, normCurrent, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(vCap.Id, currentVoiceId, StringComparison.OrdinalIgnoreCase));
            bool isPlaying = string.Equals(previewingVoiceId, vCap.Id, StringComparison.OrdinalIgnoreCase);

            var row = new Grid { ColumnSpacing = 9 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            string? brandKey = ResolveVoiceBrandKey(vCap);
            string fallbackGlyph = vCap.Kind switch
            {
                SpeechVoiceKind.ClonedCard => "mic",
                SpeechVoiceKind.System => "laptop",
                SpeechVoiceKind.Cloud => "cloud",
                _ => "wave",
            };
            FrameworkElement icon = (FrameworkElement?)ThemeHelper.CreateBrandLogoImage(brandKey, dark, 18)
                ?? YYIcons.Make(fallbackGlyph, 14, ThemeHelper.Brush(isSel || isPlaying ? tc.Accent : p.T2));
            Grid.SetColumn(icon, 0);
            row.Children.Add(icon);

            var meta = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            string cleanLabel = vCap.Label.TrimStart('✦', '☁', ' ');
            meta.Children.Add(L10n.Skip(new TextBlock
            {
                Text = cleanLabel,
                FontSize = 12.5,
                FontWeight = (isSel || isPlaying) ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = ThemeHelper.Brush(isSel || isPlaying ? tc.Accent : p.T1),
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
            }));
            string genderZh = vCap.Gender?.ToLowerInvariant() switch
            {
                "female" => L10n.T("女声"),
                "male" => L10n.T("男声"),
                _ => "",
            };
            string sub = string.Join(" · ", new[] { isPinnedCurrent ? L10n.T("当前默认") : null, vCap.Language, genderZh }.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (!string.IsNullOrEmpty(sub))
            {
                meta.Children.Add(L10n.Skip(new TextBlock
                {
                    Text = sub,
                    FontSize = 10.5,
                    Foreground = ThemeHelper.Brush(isPlaying ? tc.Accent : p.T3),
                }));
            }
            Grid.SetColumn(meta, 1);
            row.Children.Add(meta);

            var previewInner = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            previewInner.Children.Add(YYIcons.Make(isPlaying ? "stop" : "play", 10, ThemeHelper.Brush(isPlaying ? Colors.White : p.T1), filled: true));
            previewInner.Children.Add(L10n.Skip(new TextBlock
            {
                Text = L10n.T(isPlaying ? "停止" : "试听"),
                FontSize = 11,
                Foreground = ThemeHelper.Brush(isPlaying ? Colors.White : p.T1),
                VerticalAlignment = VerticalAlignment.Center,
            }));
            var previewBtn = new Button
            {
                Style = (Style)Application.Current.Resources[isPlaying ? "YYFluentAccentButtonStyle" : "YYFluentButtonStyle"],
                Height = 26,
                MinHeight = 26,
                Padding = new Thickness(9, 0, 9, 0),
                Content = previewInner,
                VerticalAlignment = VerticalAlignment.Center,
            };
            previewBtn.Click += async (_, _) => await TogglePreviewAsync(vCap);
            Grid.SetColumn(previewBtn, 2);
            row.Children.Add(previewBtn);

            var pickBtn = new Button
            {
                Style = (Style)Application.Current.Resources[isSel ? "YYFluentSoftAccentButtonStyle" : "YYFluentButtonStyle"],
                Height = 26,
                MinHeight = 26,
                Padding = new Thickness(10, 0, 10, 0),
                FontSize = 11,
                Content = L10n.T(isSel ? "当前" : "选用"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            pickBtn.Click += (_, _) =>
            {
                previewSeq++;
                previewingVoiceId = null;
                AudioPlayer.StopActivePreview();
                onPicked(vCap.Id);
                dialogOpen = false;
                dlgRef?.Hide();
            };
            Grid.SetColumn(pickBtn, 3);
            row.Children.Add(pickBtn);

            return new Border
            {
                CornerRadius = new CornerRadius(6),
                Background = ThemeHelper.Brush(isSel || isPlaying ? tc.Soft : p.Card),
                BorderBrush = ThemeHelper.Brush(isSel || isPlaying ? tc.Edge : p.Line),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 7, 10, 7),
                Child = row,
            };
        }

        void RebuildList()
        {
            if (!dialogOpen) return;
            listStack.Children.Clear();

            string query = (searchBox.Text ?? "").Trim();
            string[] terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            // Pinned currently active voice (when no search query filtering it out)
            SpeechVoiceOption? currentOpt = !string.IsNullOrEmpty(normCurrent)
                ? allVoices.FirstOrDefault(v =>
                    string.Equals(DramaSynthService.NormalizeVoiceId(v.Id) ?? v.Id, normCurrent, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(v.Id, currentVoiceId, StringComparison.OrdinalIgnoreCase))
                : null;

            var filtered = allVoices.Where(v =>
            {
                if (activeSource != "all" && ResolveVoiceSourceFilterKey(v) != activeSource) return false;
                if (!MatchesLanguageTag(v, activeLang)) return false;
                if (terms.Length > 0)
                {
                    string hay = $"{v.Label} {v.Id} {v.Language ?? ""} {v.Gender ?? ""} {(v.Gender == "female" ? "女声" : v.Gender == "male" ? "男声" : "")}";
                    if (!terms.All(t => hay.Contains(t, StringComparison.OrdinalIgnoreCase))) return false;
                }
                return true;
            }).ToList();

            if (currentOpt is not null && terms.Length == 0 && (activeSource == "all" || ResolveVoiceSourceFilterKey(currentOpt) == activeSource))
            {
                listStack.Children.Add(new TextBlock
                {
                    Text = L10n.T("当前正在用"),
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = ThemeHelper.Brush(tc.Accent),
                    Margin = new Thickness(2, 0, 0, 0),
                });
                listStack.Children.Add(MakeVoiceRowCard(currentOpt, isPinnedCurrent: true));
            }

            if (filtered.Count == 0)
            {
                listStack.Children.Add(new TextBlock
                {
                    Text = L10n.T("没有匹配的声音，试试切换上方来源或清空搜索词"),
                    FontSize = 12,
                    Foreground = ThemeHelper.Brush(p.T3),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(4, 22, 4, 22),
                });
                return;
            }

            void AddSection(string heading, List<SpeechVoiceOption> items, string? brandKey = null)
            {
                var shown = currentOpt is not null && terms.Length == 0
                    ? items.Where(x => !string.Equals(x.Id, currentOpt.Id, StringComparison.OrdinalIgnoreCase)).ToList()
                    : items;
                if (shown.Count == 0) return;

                var headRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Margin = new Thickness(2, listStack.Children.Count == 0 ? 0 : 6, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                if (!string.IsNullOrEmpty(brandKey) && ThemeHelper.CreateBrandLogoImage(brandKey, dark, 14) is { } bImg)
                    headRow.Children.Add(bImg);
                headRow.Children.Add(L10n.Skip(new TextBlock
                {
                    Text = $"{heading} ({shown.Count})",
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = ThemeHelper.Brush(p.T3),
                    VerticalAlignment = VerticalAlignment.Center,
                }));
                listStack.Children.Add(headRow);

                foreach (var v in shown)
                    listStack.Children.Add(MakeVoiceRowCard(v, isPinnedCurrent: false));
            }

            if (allVoices.All(v => v.Kind == SpeechVoiceKind.System) && terms.Length == 0)
            {
                var hintGrid = new Grid { ColumnSpacing = 10 };
                hintGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                hintGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                hintGrid.Children.Add(new TextBlock
                {
                    Text = L10n.T("当前仅启用系统自带合成音，可一键恢复免 Key 在线自然语音"),
                    FontSize = 11.5,
                    Foreground = ThemeHelper.Brush(p.T2),
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                var restoreBtn = new Button
                {
                    Style = (Style)Application.Current.Resources["YYFluentAccentButtonStyle"],
                    Height = 26,
                    MinHeight = 26,
                    Padding = new Thickness(10, 0, 10, 0),
                    FontSize = 11,
                    Content = L10n.T("恢复在线声音"),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                restoreBtn.Click += (_, _) =>
                {
                    library.Secrets.Set("tts.edge.dismissed", "");
                    library.Secrets.Set("tts.free.dismissed", "");
                    library.SavePrefs();
                    App.CurrentApp?.BroadcastVoiceChanged();
                    allVoices.Clear();
                    allVoices.AddRange(AudioPlayer.GetAllVoiceOptions(library, bookId));
                    RebuildSourceTabsData();
                    RefreshSourcePills();
                    RebuildList();
                };
                Grid.SetColumn(restoreBtn, 1);
                hintGrid.Children.Add(restoreBtn);
                listStack.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(6),
                    Background = ThemeHelper.Brush(tc.Soft),
                    BorderBrush = ThemeHelper.Brush(tc.Edge),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(12, 8, 10, 8),
                    Margin = new Thickness(0, 0, 0, 4),
                    Child = hintGrid,
                });
            }

            AddSection(L10n.T("推荐 · 语阅离线语音"), filtered.Where(v => v.Kind == SpeechVoiceKind.Kokoro).ToList());
            AddSection(L10n.T("我的 · 克隆声音"), filtered.Where(v => v.Kind == SpeechVoiceKind.ClonedCard).ToList());
            AddSection(L10n.T("我的 · 本书角色"), filtered.Where(v => v.Kind == SpeechVoiceKind.BookCard).ToList());
            AddSection(L10n.T("推荐 · 角色声音"), filtered.Where(v => v.Kind == SpeechVoiceKind.BuiltinCard).ToList());

            // Group cloud & local voices cleanly by each individual provider with brand logo!
            foreach (var prov in CloudVoiceService.AllProviders)
            {
                string pKey = CloudVoiceEntry.ProviderKey(prov);
                var provItems = filtered.Where(v => v.Kind == SpeechVoiceKind.Cloud
                    && CloudVoiceService.TryParseFullId(v.Id, out var cp, out _) && cp == prov).ToList();
                if (provItems.Count > 0)
                    AddSection(CloudVoiceService.ProviderDisplayName(prov, library.Prefs), provItems, brandKey: pKey);
            }

            AddSection(L10n.T("系统自带（不推荐 · 机械感较重）"), filtered.Where(v => v.Kind == SpeechVoiceKind.System).ToList());
        }

        rebuildListRef = RebuildList;

        // Language toggle + search row
        var searchLangGrid = new Grid { ColumnSpacing = 8 };
        searchLangGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        searchLangGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(searchBox, 0);
        searchLangGrid.Children.Add(searchBox);

        var langSeg = YYChrome.MakeSegmentedBar(
            [("zh", "中文"), ("en", "英文"), ("all", "全部")],
            activeLang,
            val =>
            {
                activeLang = val;
                if (!userEditedSample)
                    sampleBox.Text = PickDailyAuditionQuote(activeLang == "en", quoteOffset);
                RebuildList();
            },
            dark,
            fontSize: 11.5,
            height: 32);
        Grid.SetColumn(langSeg, 1);
        searchLangGrid.Children.Add(langSeg);

        // Calm warm-stationery audition quote strip: label + borderless serif line + subtle actions
        var auditionRow = new Grid { ColumnSpacing = 4 };
        auditionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        auditionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        auditionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        auditionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var quoteTag = new TextBlock
        {
            Text = L10n.T("试听句"),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeHelper.Brush(p.T3),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 4, 0),
        };
        Grid.SetColumn(quoteTag, 0);
        auditionRow.Children.Add(quoteTag);

        Grid.SetColumn(sampleBox, 1);
        auditionRow.Children.Add(sampleBox);

        var cycleQuoteBtn = new Button
        {
            Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
            Height = 26,
            MinHeight = 26,
            Padding = new Thickness(7, 0, 7, 0),
            FontSize = 11.5,
            Foreground = ThemeHelper.Brush(p.T2),
            Content = L10n.T("换一句"),
        };
        cycleQuoteBtn.Click += (_, _) =>
        {
            quoteOffset++;
            userEditedSample = false;
            sampleBox.Text = PickDailyAuditionQuote(activeLang == "en", quoteOffset);
        };
        Grid.SetColumn(cycleQuoteBtn, 2);
        auditionRow.Children.Add(cycleQuoteBtn);

        var scenesBtn = new Button
        {
            Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
            Height = 26,
            MinHeight = 26,
            Padding = new Thickness(7, 0, 7, 0),
            FontSize = 11.5,
            Foreground = ThemeHelper.Brush(p.T2),
            Content = L10n.T("场景 ▾"),
        };
        var scenesMenu = new MenuFlyout();
        void AddScene(string label, Func<string> getLine)
        {
            var mi = new MenuFlyoutItem { Text = L10n.T(label) };
            mi.Click += (_, _) =>
            {
                userEditedSample = true;
                sampleBox.Text = getLine();
            };
            scenesMenu.Items.Add(mi);
        }
        AddScene("每日一言", () => { userEditedSample = false; return PickDailyAuditionQuote(activeLang == "en", ++quoteOffset); });
        AddScene("人物对话", () => activeLang == "en"
            ? "\"Is it you?\" she asked softly. \"Of course it is,\" he replied."
            : "“这是谁？”我轻声问。“是我呀，你忘了么？”");
        AddScene("数字与日期", () => activeLang == "en"
            ? "Chapter 12, page 304: the year was 1859, at 3:40 PM."
            : "第 12 章，1986 年 3 月 4 日，下午 3 点 40 分。");
        AddScene("中英夹杂", () => "Hello，今天读到 Chapter 3 了，这段节奏很舒服。");
        scenesBtn.Flyout = scenesMenu;
        Grid.SetColumn(scenesBtn, 3);
        auditionRow.Children.Add(scenesBtn);

        var auditionStrip = new Border
        {
            CornerRadius = new CornerRadius(6),
            Background = ThemeHelper.Brush(p.Inset),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 2, 6, 2),
            Child = auditionRow,
        };

        searchBox.TextChanged += (_, _) => RebuildList();
        RebuildSourceTabsData();
        RefreshSourcePills();
        RebuildList();

        var root = new StackPanel
        {
            Spacing = 9,
            Padding = new Thickness(20, 14, 20, 14),
        };
        root.Children.Add(YYChrome.MakeDialogHeader(
            title,
            string.IsNullOrWhiteSpace(roleName)
                ? L10n.T($"共 {allVoices.Count} 个可用音色 · 点「试听」即时对比")
                : L10n.T($"为「{roleName}」挑选专属声音"),
            () => { dialogOpen = false; AudioPlayer.StopActivePreview(); dlgRef?.Hide(); },
            dark,
            titleSize: 16));
        root.Children.Add(sourceScroller);
        root.Children.Add(auditionStrip);
        root.Children.Add(searchLangGrid);
        root.Children.Add(new ScrollViewer
        {
            MaxHeight = 350,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = listStack,
        });

        dlgRef = YYChrome.CreateFluentDialog(xamlRoot, dark, 600, root, tint: library.Prefs.Tint);
        dlgRef.Closed += (_, _) =>
        {
            dialogOpen = false;
            previewSeq++;
            previewingVoiceId = null;
            AudioPlayer.StopActivePreview();
        };
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
        ApplyTtsRate(rates[(idx + 1) % rates.Length]);
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
