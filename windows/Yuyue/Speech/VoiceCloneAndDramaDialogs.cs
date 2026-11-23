using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using Windows.Media.Capture;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.UI;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;
using Yuyue.Core;
using Yuyue.Design;

namespace Yuyue.Speech;

public sealed class DramaWorkerState
{
    public CancellationTokenSource Cts { get; } = new();
    public uint FocusSection;
    public bool SectionOnly;
    public long SentChars;
    public string? LastError;
}

/// <summary>
/// 1:1 port of <c>CloneVoiceView.swift</c>, <c>ClonedVoice.swift</c>, <c>DramaWork.swift</c>, and <c>DramaViews.swift</c>:
/// <list type="bullet">
///   <item>Consent-gated zh/en Voice Cloning with <see cref="YuyueCore.CardCheck"/> quality gate, live timer,
///     pre-save audition (<see cref="CardVoiceEngine.PreviewReferenceWavAsync"/>), assignment chooser, and
///     <c>ClonedCardDetailView</c> (rename, sync toggle, 6-style emotional clips, ElevenLabs toggle, clean delete);</item>
///   <item>2-way concurrent LLM Speaker Recognition worker respecting <c>MaxOutputTokens</c>, <c>sectionOnly</c>,
///     system-prompt schema caching, dynamic focus section, and live error/char reporting;</item>
///   <item>Searchable <c>PeoplePicker</c>, spoiler-free <c>UnsureView</c> with <c>1</c>/<c>2</c> shortcuts,
///     and full <c>RoleDetail</c> (gender, aliases, rate 0.7–1.4×, pitch -6..+6, audition, auto-cast reset).</item>
/// </list>
/// </summary>
public static class VoiceCloneAndDramaDialogs
{
    private static readonly HttpClient Http = new() { Timeout = System.TimeSpan.FromSeconds(60) };
    private static readonly ConcurrentDictionary<string, DramaWorkerState> ActiveDramaWorkers = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string?> LastWorkerErrors = new(StringComparer.Ordinal);

    public static readonly (CardStyle Style, string Label, string FileSlug)[] EmotionStyles =
    [
        (CardStyle.Joy, "高兴", "joy"),
        (CardStyle.Anger, "生气", "anger"),
        (CardStyle.Sad, "难过", "sad"),
        (CardStyle.Tense, "紧张", "tense"),
        (CardStyle.Soft, "低声", "soft"),
        (CardStyle.Shout, "喊", "shout"),
    ];

    // ---------- 1. Voice Cloning Modal (1:1 CloneVoiceView.swift) ----------

    public static async Task<VoiceCard?> ShowCloneVoiceDialogAsync(
        Window ownerWindow, XamlRoot xamlRoot, LibraryService library, string? forBookId = null, string? forRoleName = null)
    {
        bool dark = ThemeHelper.IsDark(xamlRoot.Content is FrameworkElement fe ? fe.ActualTheme : ElementTheme.Light);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(library.Prefs.Tint, dark);

        string lang = "zh";
        string LoadScript(string l)
        {
            try { return library.Core.CardScript(l, CardStyle.Neutral); }
            catch
            {
                return l == "en"
                    ? "The morning light drifted softly across the quiet desk as I opened the book to read."
                    : "春天的风轻轻吹过窗台，翻开书页的声音格外安静，每一句话都带着温度。";
            }
        }

        string scriptText = LoadScript(lang);
        bool consented = false;
        float[]? capturedSamples24k = null;
        CardCheck? latestCheck = null;
        VoiceCard? savedCard = null;
        string assignTarget = forRoleName is not null ? "role" : "keep"; // keep | narrator | role

        MediaCapture? capture = null;
        InMemoryRandomAccessStream? recStream = null;
        bool isRecording = false;
        DateTime recStartUtc = DateTime.UtcNow;
        var recTimer = new DispatcherTimer { Interval = System.TimeSpan.FromMilliseconds(200) };

        var langRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var zhLangBtn = new Button { Style = (Style)Application.Current.Resources["YYPrimaryPillButtonStyle"], Content = "中文", Height = 26, FontSize = 11.5 };
        var enLangBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "English", Height = 26, FontSize = 11.5 };
        langRow.Children.Add(new TextBlock { Text = "语种", FontSize = 12, Foreground = ThemeHelper.Brush(p.T2), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
        langRow.Children.Add(zhLangBtn);
        langRow.Children.Add(enLangBtn);

        var nameBox = new TextBox { Header = "给这个声音起个名字", PlaceholderText = forRoleName is not null ? $"{forRoleName}的声音" : "例如：我的声音", Text = forRoleName ?? "" };
        var scriptBox = new TextBox
        {
            Header = "朗读参考文本（录 6–15 秒清晰人声）",
            Text = scriptText,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
        };

        void SwitchLang(string next)
        {
            lang = next;
            scriptText = LoadScript(lang);
            scriptBox.Text = scriptText;
            zhLangBtn.Style = (Style)Application.Current.Resources[lang == "zh" ? "YYPrimaryPillButtonStyle" : "YYPillButtonStyle"];
            enLangBtn.Style = (Style)Application.Current.Resources[lang == "en" ? "YYPrimaryPillButtonStyle" : "YYPillButtonStyle"];
        }
        zhLangBtn.Click += (_, _) => SwitchLang("zh");
        enLangBtn.Click += (_, _) => SwitchLang("en");

        var consentCheck = new CheckBox
        {
            Content = "这是我的声音，或者我得到了本人同意",
            FontSize = 12,
        };

        var recBtn = new Button { Style = (Style)Application.Current.Resources["YYPrimaryPillButtonStyle"], Content = "🎙 开始录音", IsEnabled = false };
        var importBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "导入一段录音…", IsEnabled = false };
        var auditionBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "▶ 试听克隆效果", IsEnabled = false, Visibility = Visibility.Collapsed };
        var useElevenCheck = new CheckBox
        {
            Content = "同时上传到 ElevenLabs 并启用云端克隆朗读",
            IsChecked = false,
            Visibility = library.CloudVoices.IsConfigured(CloudTtsProvider.ElevenLabs) ? Visibility.Visible : Visibility.Collapsed,
            FontSize = 11.5,
        };

        // "用在哪？" chooser
        var assignCombo = new ComboBox { Header = "保存后用在哪？", HorizontalAlignment = HorizontalAlignment.Stretch };
        assignCombo.Items.Add(new ComboBoxItem { Content = "先放着（仅保存到我的声音）", Tag = "keep" });
        assignCombo.Items.Add(new ComboBoxItem { Content = "设为全书默认旁白", Tag = "narrator" });
        if (forRoleName is not null)
            assignCombo.Items.Add(new ComboBoxItem { Content = $"立即用于「{forRoleName}」", Tag = "role" });
        assignCombo.SelectedIndex = forRoleName is not null ? 2 : 0;
        assignCombo.SelectionChanged += (_, _) =>
        {
            if (assignCombo.SelectedItem is ComboBoxItem ci && ci.Tag is string tag) assignTarget = tag;
        };

        var checkCardHost = new StackPanel { Spacing = 4 };
        ContentDialog? dlgRef = null;

        void UpdateSaveGate()
        {
            bool ok = capturedSamples24k is { Length: >= 24_000 } && latestCheck is { Ok: true };
            if (dlgRef is not null) dlgRef.IsPrimaryButtonEnabled = ok;
            auditionBtn.Visibility = ok && library.LiveVoice.IsModelInstalled ? Visibility.Visible : Visibility.Collapsed;
            auditionBtn.IsEnabled = ok && library.LiveVoice.IsModelInstalled;
        }

        void RenderCheckResult(CardCheck chk)
        {
            checkCardHost.Children.Clear();
            if (chk.Ok)
            {
                checkCardHost.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(9),
                    Background = ThemeHelper.Brush(Color.FromArgb(26, p.Ok.R, p.Ok.G, p.Ok.B)),
                    Padding = new Thickness(12, 8, 12, 8),
                    Child = new TextBlock
                    {
                        Text = $"✓ 录音质量合格（时长 {chk.Seconds:0.1} 秒 · 信噪比 {chk.SnrDb:0} dB）",
                        FontSize = 12,
                        Foreground = ThemeHelper.Brush(p.Ok),
                    },
                });
            }
            else
            {
                string reasons = chk.Messages.Length > 0
                    ? string.Join("；", chk.Messages)
                    : $"录音未通过质量门控（时长 {chk.Seconds:0.1}s，音量 {chk.SpeechDb:0} dBFS，信噪比 {chk.SnrDb:0} dB），请在安静环境下录 6–15 秒清晰语音。";
                checkCardHost.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(9),
                    Background = ThemeHelper.Brush(Color.FromArgb(26, p.Danger.R, p.Danger.G, p.Danger.B)),
                    Padding = new Thickness(12, 8, 12, 8),
                    Child = new TextBlock
                    {
                        Text = "✕ " + reasons,
                        FontSize = 11.5,
                        Foreground = ThemeHelper.Brush(p.Danger),
                        TextWrapping = TextWrapping.Wrap,
                    },
                });
            }
            UpdateSaveGate();
        }

        recTimer.Tick += (_, _) =>
        {
            if (isRecording)
                recBtn.Content = $"⏹ 停止录音（{(DateTime.UtcNow - recStartUtc).TotalSeconds:0.0}s）";
        };

        consentCheck.Checked += (_, _) => { consented = true; recBtn.IsEnabled = true; importBtn.IsEnabled = true; };
        consentCheck.Unchecked += (_, _) => { consented = false; recBtn.IsEnabled = false; importBtn.IsEnabled = false; };

        recBtn.Click += async (_, _) =>
        {
            if (!consented) return;
            if (!isRecording)
            {
                try
                {
                    capture = new MediaCapture();
                    await capture.InitializeAsync(new MediaCaptureInitializationSettings { StreamingCaptureMode = StreamingCaptureMode.Audio });
                    recStream = new InMemoryRandomAccessStream();
                    var profile = MediaEncodingProfile.CreateWav(AudioEncodingQuality.High);
                    profile.Audio.SampleRate = 24_000;
                    profile.Audio.ChannelCount = 1;
                    profile.Audio.BitsPerSample = 16;
                    await capture.StartRecordToStreamAsync(profile, recStream);
                    isRecording = true;
                    recStartUtc = DateTime.UtcNow;
                    recTimer.Start();
                    recBtn.Content = "⏹ 停止录音（0.0s）";
                }
                catch (Exception ex)
                {
                    checkCardHost.Children.Clear();
                    checkCardHost.Children.Add(new TextBlock { Text = $"无法打开麦克风：{ex.Message}", FontSize = 11.5, Foreground = ThemeHelper.Brush(p.Danger), TextWrapping = TextWrapping.Wrap });
                }
            }
            else
            {
                isRecording = false;
                recTimer.Stop();
                recBtn.Content = "🎙 重新录音";
                try
                {
                    if (capture is not null) await capture.StopRecordAsync();
                    if (recStream is not null && recStream.Size > 44)
                    {
                        recStream.Seek(0);
                        var wavBytes = new byte[recStream.Size];
                        await recStream.ReadAsync(wavBytes.AsBuffer(), (uint)wavBytes.Length, InputStreamOptions.None);
                        string tmpWav = Path.Combine(Path.GetTempPath(), $"yuyue-rec-{Guid.NewGuid():N}.wav");
                        await File.WriteAllBytesAsync(tmpWav, wavBytes);
                        try
                        {
                            capturedSamples24k = library.Core.CardDecodeClip(tmpWav, 24_000);
                            latestCheck = library.Core.CardCheck(capturedSamples24k, 24_000, scriptBox.Text.Trim(), null);
                            RenderCheckResult(latestCheck);
                        }
                        finally { try { File.Delete(tmpWav); } catch { } }
                    }
                }
                catch (Exception ex)
                {
                    checkCardHost.Children.Clear();
                    checkCardHost.Children.Add(new TextBlock { Text = $"处理录音失败：{ex.Message}", FontSize = 11.5, Foreground = ThemeHelper.Brush(p.Danger) });
                }
                finally
                {
                    recStream?.Dispose(); recStream = null;
                    capture?.Dispose(); capture = null;
                }
            }
        };

        importBtn.Click += async (_, _) =>
        {
            if (!consented) return;
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".wav");
            picker.FileTypeFilter.Add(".mp3");
            picker.FileTypeFilter.Add(".m4a");
            picker.FileTypeFilter.Add(".flac");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(ownerWindow));
            var f = await picker.PickSingleFileAsync();
            if (f is null) return;
            try
            {
                capturedSamples24k = await Task.Run(() => library.Core.CardDecodeClip(f.Path, 24_000));
                latestCheck = await Task.Run(() => library.Core.CardCheck(capturedSamples24k, 24_000, scriptBox.Text.Trim(), null));
                RenderCheckResult(latestCheck);
                if (string.IsNullOrWhiteSpace(nameBox.Text))
                    nameBox.Text = Path.GetFileNameWithoutExtension(f.Name);
            }
            catch (Exception ex)
            {
                checkCardHost.Children.Clear();
                checkCardHost.Children.Add(new TextBlock { Text = $"解码音频失败：{ex.Message}", FontSize = 11.5, Foreground = ThemeHelper.Brush(p.Danger), TextWrapping = TextWrapping.Wrap });
            }
        };

        auditionBtn.Click += async (_, _) =>
        {
            if (capturedSamples24k is null) return;
            auditionBtn.IsEnabled = false;
            auditionBtn.Content = "合成试听中…";
            try
            {
                string sample = lang == "en"
                    ? "Hello, this is a preview of your newly cloned voice reading aloud."
                    : "你好，这是用你刚录制的声音试读的一段书页文字。";
                byte[]? wav = await CardVoiceEngine.Instance.PreviewReferenceWavAsync(
                    library.Core, library.LiveVoice, capturedSamples24k, 24_000, scriptBox.Text.Trim(), sample, lang);
                if (wav is { Length: > 44 })
                    await PlayWavBytesOnceAsync(wav);
            }
            catch { }
            finally
            {
                auditionBtn.Content = "▶ 试听克隆效果";
                auditionBtn.IsEnabled = true;
            }
        };

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { recBtn, importBtn, auditionBtn } };
        var root = new StackPanel
        {
            Width = 440,
            Spacing = 10,
            Children = { langRow, nameBox, scriptBox, consentCheck, btnRow, checkCardHost, assignCombo, useElevenCheck },
        };

        dlgRef = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "克隆一段声音",
            Content = root,
            PrimaryButtonText = "保存声音卡",
            IsPrimaryButtonEnabled = false,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        var res = await dlgRef.ShowAsync();
        recTimer.Stop();
        if (isRecording && capture is not null)
        {
            try { await capture.StopRecordAsync(); } catch { }
        }
        recStream?.Dispose();
        capture?.Dispose();

        if (res != ContentDialogResult.Primary || capturedSamples24k is null || capturedSamples24k.Length < 24_000 || latestCheck is not { Ok: true })
            return null;

        string cardId = $"cloned.{Guid.NewGuid():N}"[..19];
        string cardName = string.IsNullOrWhiteSpace(nameBox.Text) ? "我的声音" : nameBox.Text.Trim();
        string stageDir = Path.Combine(library.DataDir, "clones", cardId);
        Directory.CreateDirectory(stageDir);
        string wavFile = Path.Combine(stageDir, "neutral.wav");

        try
        {
            float secs = library.Core.CardWriteWav(wavFile, capturedSamples24k, 24_000);
            CharacterGender gender = CharacterGender.Unknown;
            try { gender = library.Core.SpeakerVoiceGender(capturedSamples24k, 24_000); } catch { }

            string spokenTxt = string.IsNullOrWhiteSpace(scriptBox.Text) ? scriptText : scriptBox.Text.Trim();
            float avgRate = Math.Max(2.5f, spokenTxt.Length / Math.Max(1.0f, secs));

            var cardDraft = new VoiceCard(
                Id: cardId,
                Name: cardName,
                Gender: gender,
                Age: CharacterAge.Young,
                Tags: ["克隆"],
                Lang: lang,
                Origin: new CardOrigin.Cloned(),
                Clips: [new CardClip(CardStyle.Neutral, "neutral.wav", spokenTxt, secs)],
                Description: "本机克隆声音",
                Version: 1,
                Narrator: true,
                Rate: avgRate,
                Embedding: [],
                Sync: false);

            savedCard = await Task.Run(() => library.Core.CardSave(null, cardDraft, stageDir));

            if (useElevenCheck.IsChecked == true && library.CloudVoices.IsConfigured(CloudTtsProvider.ElevenLabs))
            {
                string? elId = await UploadElevenLabsCloneAsync(library, cardName, wavFile);
                if (!string.IsNullOrEmpty(elId))
                {
                    library.Prefs.ClonedElevenLabsVoiceIds[savedCard.Id] = elId;
                    library.Prefs.ClonedUseElevenLabs.Add(savedCard.Id);
                    library.SavePrefs();
                }
            }

            if (assignTarget == "role" && forBookId is not null && forRoleName is not null)
            {
                try { library.Core.SetBookRole(forBookId, forRoleName, new RoleVoice(savedCard.Id, 1.0, 0.0, false)); }
                catch { }
            }
            else if (assignTarget == "narrator")
            {
                library.Prefs.SetVoiceForLanguage(lang, savedCard.Id);
                library.SavePrefs();
            }

            DramaSynthService.InvalidateAllCasts();
            App.CurrentApp.BroadcastVoiceChanged();
            return savedCard;
        }
        catch (Exception ex)
        {
            await new ContentDialog { XamlRoot = xamlRoot, Title = "保存克隆声音失败", Content = ex.Message, CloseButtonText = "好" }.ShowAsync();
            return null;
        }
    }

    /// <summary>
    /// 1:1 <c>ClonedCardDetailView</c> + <c>StyleClipRecordSheet</c> (<c>CloneVoiceView.swift:762-1206</c>):
    /// rename, sync toggle, ElevenLabs opt-in toggle, 6 emotional style clips (record/import/preview), and clean delete.
    /// </summary>
    public static async Task ShowClonedCardDetailDialogAsync(
        Window ownerWindow, XamlRoot xamlRoot, LibraryService library, string cardId, Action? onChanged = null)
    {
        bool dark = ThemeHelper.IsDark(xamlRoot.Content is FrameworkElement fe ? fe.ActualTheme : ElementTheme.Light);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(library.Prefs.Tint, dark);

        var host = new StackPanel { Width = 440, Spacing = 10 };
        ContentDialog? dlgRef = null;
        bool deleted = false;
        CardStyle? pendingRecordStyle = null;

        void Rebuild()
        {
            host.Children.Clear();
            var card = library.LiveVoice.GetClonedCards().FirstOrDefault(c => c.Id == cardId);
            if (card is null)
            {
                host.Children.Add(new TextBlock { Text = "找不到该克隆声音卡。", Foreground = ThemeHelper.Brush(p.T2) });
                return;
            }

            // 1. Rename row
            var nameRow = new Grid { ColumnSpacing = 8 };
            nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var nameBox = new TextBox { Header = "声音名称", Text = card.Name };
            var renameBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "保存名称", VerticalAlignment = VerticalAlignment.Bottom, Height = 32 };
            renameBtn.Click += (_, _) =>
            {
                string next = nameBox.Text.Trim();
                if (string.IsNullOrEmpty(next) || next == card.Name) return;
                try
                {
                    string dir = Path.Combine(library.DataDir, "clones", card.Id);
                    Directory.CreateDirectory(dir);
                    library.Core.CardSave(null, card with { Name = next, Version = card.Version + 1 }, dir);
                    App.CurrentApp.BroadcastVoiceChanged();
                    onChanged?.Invoke();
                    Rebuild();
                }
                catch { }
            };
            nameRow.Children.Add(nameBox);
            Grid.SetColumn(renameBtn, 1);
            nameRow.Children.Add(renameBtn);
            host.Children.Add(nameRow);

            // 2. Sync & ElevenLabs toggles
            var syncRow = new Grid();
            syncRow.Children.Add(new TextBlock { Text = "跨设备跟着同步", FontSize = 12.5, Foreground = ThemeHelper.Brush(p.T1), VerticalAlignment = VerticalAlignment.Center });
            var syncSw = new ToggleSwitch { IsOn = card.Sync, HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 0, OnContent = "", OffContent = "" };
            syncSw.Toggled += (_, _) =>
            {
                try
                {
                    string dir = Path.Combine(library.DataDir, "clones", card.Id);
                    Directory.CreateDirectory(dir);
                    library.Core.CardSave(null, card with { Sync = syncSw.IsOn, Version = card.Version + 1 }, dir);
                    onChanged?.Invoke();
                }
                catch { }
            };
            syncRow.Children.Add(syncSw);
            host.Children.Add(syncRow);

            if (library.CloudVoices.IsConfigured(CloudTtsProvider.ElevenLabs))
            {
                var elRow = new Grid();
                elRow.Children.Add(new TextBlock { Text = "优先使用 ElevenLabs 云端朗读", FontSize = 12.5, Foreground = ThemeHelper.Brush(p.T1), VerticalAlignment = VerticalAlignment.Center });
                var elSw = new ToggleSwitch { IsOn = library.Prefs.ClonedUseElevenLabs.Contains(card.Id), HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 0, OnContent = "", OffContent = "" };
                elSw.Toggled += async (_, _) =>
                {
                    if (elSw.IsOn)
                    {
                        if (!library.Prefs.ClonedElevenLabsVoiceIds.ContainsKey(card.Id))
                        {
                            string? neutralPath = library.Core.CardClipPath(null, card.Id, CardStyle.Neutral);
                            if (!string.IsNullOrEmpty(neutralPath) && File.Exists(neutralPath))
                            {
                                string? elId = await UploadElevenLabsCloneAsync(library, card.Name, neutralPath);
                                if (!string.IsNullOrEmpty(elId))
                                    library.Prefs.ClonedElevenLabsVoiceIds[card.Id] = elId;
                            }
                        }
                        library.Prefs.ClonedUseElevenLabs.Add(card.Id);
                    }
                    else
                    {
                        library.Prefs.ClonedUseElevenLabs.Remove(card.Id);
                    }
                    library.SavePrefs();
                    App.CurrentApp.BroadcastVoiceChanged();
                };
                elRow.Children.Add(elSw);
                host.Children.Add(elRow);
            }

            // 3. 6 Emotional Style Clips
            host.Children.Add(new TextBlock { Text = "6 种情绪样本（广播剧带语气演绎时自动匹配）", FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T3), Margin = new Thickness(0, 4, 0, 0) });
            var stylesStack = new StackPanel { Spacing = 5 };
            foreach (var (st, lbl, slug) in EmotionStyles)
            {
                var existing = card.Clips.FirstOrDefault(c => c.Style == st);
                var rg = new Grid { ColumnSpacing = 8 };
                rg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                rg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                rg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var meta = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
                meta.Children.Add(new TextBlock { Text = lbl, FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1) });
                meta.Children.Add(new TextBlock
                {
                    Text = existing is not null ? $"已录 · {existing.Seconds:0.1} 秒" : "未录制（将沿用中性样本）",
                    FontSize = 10.5,
                    Foreground = ThemeHelper.Brush(existing is not null ? p.Ok : p.T3),
                });
                rg.Children.Add(meta);

                if (existing is not null)
                {
                    var playB = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "▶", Height = 24, MinHeight = 24, FontSize = 11 };
                    CardStyle capPlaySt = st;
                    playB.Click += async (_, _) =>
                    {
                        try
                        {
                            string? clipPath = library.Core.CardClipPath(null, card.Id, capPlaySt);
                            if (!string.IsNullOrEmpty(clipPath) && File.Exists(clipPath))
                                await PlayWavBytesOnceAsync(await File.ReadAllBytesAsync(clipPath));
                        }
                        catch { }
                    };
                    Grid.SetColumn(playB, 1);
                    rg.Children.Add(playB);
                }

                var recB = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = existing is not null ? "重录" : "录制", Height = 24, MinHeight = 24, FontSize = 11 };
                CardStyle capSt = st;
                recB.Click += (_, _) =>
                {
                    pendingRecordStyle = capSt;
                    dlgRef?.Hide();
                };
                Grid.SetColumn(recB, 2);
                rg.Children.Add(recB);

                stylesStack.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(9),
                    Background = ThemeHelper.Brush(p.Inset),
                    Padding = new Thickness(10, 6, 10, 6),
                    Child = rg,
                });
            }
            host.Children.Add(stylesStack);

            // 4. Delete button
            var delBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
                Content = "删除此克隆声音",
                Foreground = ThemeHelper.Brush(p.Danger),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 6, 0, 0),
            };
            delBtn.Click += (_, _) =>
            {
                DeleteClonedCard(library, card.Id);
                deleted = true;
                onChanged?.Invoke();
                dlgRef?.Hide();
            };
            host.Children.Add(delBtn);
        }

        while (!deleted)
        {
            pendingRecordStyle = null;
            Rebuild();
            dlgRef = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = "克隆声音详情",
                Content = new ScrollViewer { MaxHeight = 460, Content = host },
                CloseButtonText = "完成",
            };
            await dlgRef.ShowAsync();

            if (pendingRecordStyle is CardStyle targetStyle)
            {
                await ShowRecordStyleClipDialogAsync(ownerWindow, xamlRoot, library, cardId, targetStyle);
                onChanged?.Invoke();
                continue;
            }
            break;
        }
    }

    private static async Task ShowRecordStyleClipDialogAsync(
        Window ownerWindow, XamlRoot xamlRoot, LibraryService library, string cardId, CardStyle style)
    {
        var card = library.LiveVoice.GetClonedCards().FirstOrDefault(c => c.Id == cardId);
        if (card is null) return;

        bool dark = ThemeHelper.IsDark(xamlRoot.Content is FrameworkElement fe ? fe.ActualTheme : ElementTheme.Light);
        var p = ThemeHelper.Palette(dark);
        bool zh = !card.Lang.StartsWith("en", StringComparison.OrdinalIgnoreCase);

        string styleName = EmotionStyles.FirstOrDefault(x => x.Style == style).Label ?? style.ToString();
        string slug = EmotionStyles.FirstOrDefault(x => x.Style == style).FileSlug ?? style.ToString().ToLowerInvariant();
        string instruction = "";
        string script = "";
        try { instruction = library.Core.CardStyleInstruction(style, zh); } catch { }
        try { script = library.Core.CardScript(card.Lang, style); } catch { script = "请用对应情绪清晰朗读 6 到 12 秒。"; }

        float[]? samples24k = null;
        CardCheck? chk = null;
        MediaCapture? capture = null;
        InMemoryRandomAccessStream? stream = null;
        bool recording = false;

        var scriptBox = new TextBox { Header = $"示范台词（{instruction}）", Text = script, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
        var statusText = new TextBlock { FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T2), TextWrapping = TextWrapping.Wrap };
        ContentDialog? dlgRef = null;

        void ApplyCheck(CardCheck c)
        {
            chk = c;
            statusText.Foreground = ThemeHelper.Brush(c.Ok ? p.Ok : p.Danger);
            statusText.Text = c.Ok
                ? $"✓ 情绪样本合格（{c.Seconds:0.1}s · 信噪比 {c.SnrDb:0} dB）"
                : "✕ " + (c.Messages.Length > 0 ? string.Join("；", c.Messages) : "请重新录制清晰语音");
            if (dlgRef is not null) dlgRef.IsPrimaryButtonEnabled = c.Ok;
        }

        var recBtn = new Button { Style = (Style)Application.Current.Resources["YYPrimaryPillButtonStyle"], Content = "🎙 开始录音" };
        recBtn.Click += async (_, _) =>
        {
            if (!recording)
            {
                try
                {
                    capture = new MediaCapture();
                    await capture.InitializeAsync(new MediaCaptureInitializationSettings { StreamingCaptureMode = StreamingCaptureMode.Audio });
                    stream = new InMemoryRandomAccessStream();
                    var prof = MediaEncodingProfile.CreateWav(AudioEncodingQuality.High);
                    prof.Audio.SampleRate = 24_000; prof.Audio.ChannelCount = 1; prof.Audio.BitsPerSample = 16;
                    await capture.StartRecordToStreamAsync(prof, stream);
                    recording = true;
                    recBtn.Content = "⏹ 停止并检测";
                }
                catch (Exception ex) { statusText.Text = $"无法录音：{ex.Message}"; }
            }
            else
            {
                recording = false;
                recBtn.Content = "🎙 重新录音";
                try
                {
                    if (capture is not null) await capture.StopRecordAsync();
                    if (stream is not null && stream.Size > 44)
                    {
                        stream.Seek(0);
                        var bytes = new byte[stream.Size];
                        await stream.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
                        string tmp = Path.Combine(Path.GetTempPath(), $"yuyue-style-{Guid.NewGuid():N}.wav");
                        await File.WriteAllBytesAsync(tmp, bytes);
                        try
                        {
                            samples24k = library.Core.CardDecodeClip(tmp, 24_000);
                            ApplyCheck(library.Core.CardCheck(samples24k, 24_000, scriptBox.Text.Trim(), null));
                        }
                        finally { try { File.Delete(tmp); } catch { } }
                    }
                }
                catch (Exception ex) { statusText.Text = $"处理失败：{ex.Message}"; }
                finally { stream?.Dispose(); stream = null; capture?.Dispose(); capture = null; }
            }
        };

        var impBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "导入音频…" };
        impBtn.Click += async (_, _) =>
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".wav"); picker.FileTypeFilter.Add(".mp3"); picker.FileTypeFilter.Add(".m4a");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(ownerWindow));
            var f = await picker.PickSingleFileAsync();
            if (f is null) return;
            try
            {
                samples24k = await Task.Run(() => library.Core.CardDecodeClip(f.Path, 24_000));
                ApplyCheck(await Task.Run(() => library.Core.CardCheck(samples24k, 24_000, scriptBox.Text.Trim(), null)));
            }
            catch (Exception ex) { statusText.Text = $"解码失败：{ex.Message}"; }
        };

        dlgRef = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = $"录制「{styleName}」情绪样本",
            Content = new StackPanel
            {
                Width = 400,
                Spacing = 10,
                Children = { scriptBox, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { recBtn, impBtn } }, statusText },
            },
            PrimaryButtonText = "保存情绪样本",
            IsPrimaryButtonEnabled = false,
            CloseButtonText = "取消",
        };
        var res = await dlgRef.ShowAsync();
        if (recording && capture is not null) { try { await capture.StopRecordAsync(); } catch { } }
        stream?.Dispose(); capture?.Dispose();

        if (res != ContentDialogResult.Primary || samples24k is null || chk is not { Ok: true }) return;

        try
        {
            string stageDir = Path.Combine(library.DataDir, "clones", card.Id);
            Directory.CreateDirectory(stageDir);
            string fileName = $"{slug}.wav";
            float secs = library.Core.CardWriteWav(Path.Combine(stageDir, fileName), samples24k, 24_000);
            var updatedClips = card.Clips.Where(c => c.Style != style)
                .Append(new CardClip(style, fileName, scriptBox.Text.Trim(), secs))
                .ToArray();
            await Task.Run(() => library.Core.CardSave(null, card with { Clips = updatedClips, Version = card.Version + 1 }, stageDir));
            DramaSynthService.InvalidateAllCasts();
            App.CurrentApp.BroadcastVoiceChanged();
        }
        catch { }
    }

    public static void DeleteClonedCard(LibraryService library, string cardId)
    {
        try { library.Core.CardDelete(null, cardId); } catch { }
        try
        {
            string dir = Path.Combine(library.DataDir, "clones", cardId);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch { }
        library.Prefs.ClonedElevenLabsVoiceIds.Remove(cardId);
        library.Prefs.ClonedUseElevenLabs.Remove(cardId);
        if (DramaSynthService.NormalizeVoiceId(library.Prefs.VoiceId) == cardId) library.Prefs.VoiceId = "";
        if (DramaSynthService.NormalizeVoiceId(library.Prefs.VoiceIdEn) == cardId) library.Prefs.VoiceIdEn = "";
        library.SavePrefs();
        DramaSynthService.InvalidateAllCasts();
        App.CurrentApp.BroadcastVoiceChanged();
    }

    private static async Task<string?> UploadElevenLabsCloneAsync(LibraryService library, string cardName, string wavFile)
    {
        try
        {
            string elKey = library.Secrets.Get("tts.elevenlabs.key").Trim();
            if (string.IsNullOrEmpty(elKey)) return null;
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent($"语阅 · {cardName}"), "name");
            form.Add(new StringContent("true"), "remove_background_noise");
            var filePart = new ByteArrayContent(await File.ReadAllBytesAsync(wavFile));
            filePart.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(filePart, "files", "neutral.wav");

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.elevenlabs.io/v1/voices/add") { Content = form };
            req.Headers.Add("xi-api-key", elKey);
            using var resp = await Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            return doc.RootElement.TryGetProperty("voice_id", out var vp) ? vp.GetString() : null;
        }
        catch { return null; }
    }

    private static async Task PlayWavBytesOnceAsync(byte[] wav)
    {
        var mem = new InMemoryRandomAccessStream();
        await mem.WriteAsync(wav.AsBuffer());
        mem.Seek(0);
        using var mp = new MediaPlayer { AutoPlay = true };
        var done = new TaskCompletionSource();
        mp.MediaEnded += (_, _) => done.TrySetResult();
        mp.Source = MediaSource.CreateFromStream(mem, "audio/wav");
        await Task.WhenAny(done.Task, Task.Delay(15_000));
    }

    // ---------- 2. Background 2-Way Concurrent LLM Speaker Recognition Worker (1:1 DramaWork.swift) ----------

    public static bool IsDramaWorkerRunning(string bookId) => ActiveDramaWorkers.ContainsKey(bookId);

    public static (bool Running, long SentChars, string? LastError) GetDramaWorkerStatus(string bookId)
    {
        if (ActiveDramaWorkers.TryGetValue(bookId, out var st))
            return (true, Interlocked.Read(ref st.SentChars), st.LastError);
        LastWorkerErrors.TryGetValue(bookId, out var err);
        return (false, 0, err);
    }

    public static void UpdateDramaFocusSection(string bookId, uint section)
    {
        if (ActiveDramaWorkers.TryGetValue(bookId, out var st))
            st.FocusSection = section;
    }

    public static void StopDramaWorker(string bookId)
    {
        if (ActiveDramaWorkers.TryRemove(bookId, out var st))
        {
            try { st.Cts.Cancel(); } catch { }
            st.Cts.Dispose();
        }
    }

    public static void StartDramaSpeakerWorker(
        LibraryService library, string bookId, uint focusSection, Action onProgressUpdated, bool sectionOnly = false)
    {
        if (ActiveDramaWorkers.ContainsKey(bookId) || !library.Companion.IsConfigured) return;

        var state = new DramaWorkerState { FocusSection = focusSection, SectionOnly = sectionOnly };
        if (!ActiveDramaWorkers.TryAdd(bookId, state))
        {
            state.Cts.Dispose();
            return;
        }
        LastWorkerErrors.TryRemove(bookId, out _);
        var ct = state.Cts.Token;

        string lang = "zh-Hans";
        try { lang = library.GetBookDetail(bookId).Summary.Language ?? "zh-Hans"; } catch { }
        DramaSynthService.UpdateDramaCaps(library, bookId, lang, null, ttsActive: false);
        SoundLibraryService.Register(library.Core, library.DataDir);

        _ = Task.Run(async () =>
        {
            var nextTaskGate = new SemaphoreSlim(1, 1);
            const int Parallelism = 2;

            async Task RunSlotAsync()
            {
                while (!ct.IsCancellationRequested)
                {
                    DramaTask? task;
                    await nextTaskGate.WaitAsync(ct);
                    try
                    {
                        uint curFocus = state.FocusSection;
                        task = library.Core.DramaNextTask(bookId, curFocus);
                    }
                    catch (Exception ex)
                    {
                        state.LastError = ex.Message;
                        break;
                    }
                    finally { nextTaskGate.Release(); }

                    if (task is null) break;

                    if (task is DramaTask.Llm llm)
                    {
                        // Respect sectionOnly scope: task IDs for chapters carry "s{sec}:" or section scope check
                        if (state.SectionOnly && llm.Id.StartsWith("scene:", StringComparison.Ordinal))
                        {
                            var parts = llm.Id.Split(':');
                            if (parts.Length >= 2 && uint.TryParse(parts[1], out uint taskSec) && taskSec != state.FocusSection)
                            {
                                try { library.Core.DramaSubmit(bookId, llm.Id, new DramaResult.Returned()); } catch { }
                                break;
                            }
                        }

                        // Put <schema> in system prompt to maximize prompt cache hit rate (1:1 DramaWork.swift)
                        string systemPrompt = string.IsNullOrWhiteSpace(llm.Schema)
                            ? llm.System
                            : $"{llm.System}\n\n<schema>\n{llm.Schema}\n</schema>";
                        int maxTokens = llm.MaxOutputTokens > 0 ? (int)Math.Clamp(llm.MaxOutputTokens, 256u, 8192u) : 2048;

                        Interlocked.Add(ref state.SentChars, systemPrompt.Length + llm.User.Length);

                        string? answer = null;
                        string? lastErr = null;
                        int[] backoffs = [4, 12, 36];
                        for (int attempt = 0; attempt <= backoffs.Length; attempt++)
                        {
                            ct.ThrowIfCancellationRequested();
                            try
                            {
                                answer = await library.Companion.ChatAsync(
                                    llm.User,
                                    systemPrompt,
                                    maxTokens: maxTokens,
                                    fast: true,
                                    cachePrefixLen: (int)llm.CachePrefixLen,
                                    source: UsageSource.Drama,
                                    ct: ct);
                                lastErr = null;
                                break;
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception ex) when (attempt < backoffs.Length && !ex.Message.Contains("401") && !ex.Message.Contains("402") && !ex.Message.Contains("403"))
                            {
                                lastErr = ex.Message;
                                await Task.Delay(System.TimeSpan.FromSeconds(backoffs[attempt]), ct);
                            }
                            catch (Exception ex)
                            {
                                lastErr = ex.Message;
                                answer = null;
                                break;
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(answer))
                        {
                            try { library.Core.DramaSubmit(bookId, llm.Id, new DramaResult.Llm(answer)); } catch { }
                            DramaSynthService.InvalidateCast(bookId);
                            onProgressUpdated();
                        }
                        else
                        {
                            state.LastError = lastErr ?? "模型未返回有效结果";
                            LastWorkerErrors[bookId] = state.LastError;
                            try { library.Core.DramaSubmit(bookId, llm.Id, new DramaResult.Returned()); } catch { }
                            break;
                        }
                    }
                    else
                    {
                        string tid = task switch
                        {
                            DramaTask.Tts t => t.Id,
                            DramaTask.Sfx s => s.Id,
                            DramaTask.DesignVoice d => d.Id,
                            _ => "",
                        };
                        if (!string.IsNullOrEmpty(tid))
                        {
                            try { library.Core.DramaSubmit(bookId, tid, new DramaResult.Returned()); } catch { }
                        }
                        break;
                    }
                }
            }

            try
            {
                var workers = Enumerable.Range(0, Parallelism).Select(_ => RunSlotAsync()).ToArray();
                await Task.WhenAll(workers);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                state.LastError = ex.Message;
                LastWorkerErrors[bookId] = ex.Message;
            }
            finally
            {
                ActiveDramaWorkers.TryRemove(bookId, out _);
                state.Cts.Dispose();
                onProgressUpdated();
            }
        }, ct);
    }

    // ---------- 3. Searchable PeoplePicker (1:1 DramaViews.swift PeoplePicker) ----------

    public static async Task<UserChoice?> ShowPeoplePickerDialogAsync(
        XamlRoot xamlRoot, LibraryService library, string bookId, uint currentSection, string title = "选择说话人", bool includeGroup = true)
    {
        bool dark = ThemeHelper.IsDark(xamlRoot.Content is FrameworkElement fe ? fe.ActualTheme : ElementTheme.Light);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(library.Prefs.Tint, dark);

        CastPick[] everyone;
        try { everyone = library.Core.DramaEveryone(bookId, currentSection); }
        catch { everyone = []; }

        UserChoice? picked = null;
        ContentDialog? dlgRef = null;
        var searchBox = new TextBox { PlaceholderText = "搜索人物或输入新名字后回车…" };
        var listHost = new StackPanel { Spacing = 4 };

        void Rebuild(string q)
        {
            listHost.Children.Clear();
            string filter = q.Trim();

            var matches = everyone.Where(c =>
                string.IsNullOrEmpty(filter) ||
                c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).Take(40);

            foreach (var person in matches)
            {
                string nm = person.Name;
                var btn = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Background = ThemeHelper.Brush(p.Inset),
                    BorderThickness = new Thickness(0),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(10, 7, 10, 7),
                    Content = new TextBlock { Text = nm, FontSize = 12.5, Foreground = ThemeHelper.Brush(p.T1) },
                };
                btn.Click += (_, _) => { picked = new UserChoice.Person(nm); dlgRef?.Hide(); };
                listHost.Children.Add(btn);
            }

            if (includeGroup && (string.IsNullOrEmpty(filter) || "众人".Contains(filter)))
            {
                var grpBtn = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Background = ThemeHelper.Brush(p.Inset),
                    BorderThickness = new Thickness(0),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(10, 7, 10, 7),
                    Content = new TextBlock { Text = "众人（齐声）", FontSize = 12.5, Foreground = ThemeHelper.Brush(p.T2) },
                };
                grpBtn.Click += (_, _) => { picked = new UserChoice.Group("众人"); dlgRef?.Hide(); };
                listHost.Children.Add(grpBtn);
            }

            if (!string.IsNullOrEmpty(filter) && !everyone.Any(c => string.Equals(c.Name, filter, StringComparison.OrdinalIgnoreCase)))
            {
                var addBtn = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Background = ThemeHelper.Brush(tint.Soft),
                    BorderThickness = new Thickness(0),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(10, 7, 10, 7),
                    Content = new TextBlock { Text = $"＋ 新建人物「{filter}」", FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(tint.Accent) },
                };
                addBtn.Click += (_, _) => { picked = new UserChoice.Person(filter); dlgRef?.Hide(); };
                listHost.Children.Add(addBtn);
            }
        }

        searchBox.TextChanged += (_, _) => Rebuild(searchBox.Text);
        searchBox.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter && !string.IsNullOrWhiteSpace(searchBox.Text))
            {
                string q = searchBox.Text.Trim();
                var hit = everyone.FirstOrDefault(c => c.Name.Contains(q, StringComparison.OrdinalIgnoreCase));
                picked = new UserChoice.Person(hit?.Name ?? q);
                dlgRef?.Hide();
            }
        };
        Rebuild("");

        dlgRef = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = title,
            Content = new StackPanel { Width = 360, Spacing = 8, Children = { searchBox, new ScrollViewer { MaxHeight = 280, Content = listHost } } },
            CloseButtonText = "取消",
        };
        await dlgRef.ShowAsync();
        return picked;
    }

    // ---------- 4. Active-Learning Unsure Speaker Review (1:1 DramaViews.swift, spoiler-free + 1/2 shortcuts) ----------

    public static async Task ShowUnsureSpeakerReviewAsync(
        XamlRoot xamlRoot, LibraryService library, string bookId, uint currentSection, Action onChanged)
    {
        bool dark = ThemeHelper.IsDark(xamlRoot.Content is FrameworkElement fe ? fe.ActualTheme : ElementTheme.Light);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(library.Prefs.Tint, dark);

        int skipOffset = 0;
        int resolvedCount = 0;
        (uint Sec, uint Sent, uint Start, string Name, uint Freed)? lastDecision = null;
        UnsureLine? activeLine = null;
        var host = new StackPanel { Width = 440, Spacing = 10 };
        ContentDialog? dlgRef = null;
        bool openPickerNext = false;

        void CommitChoice(UnsureLine cur, UserChoice choice, string displayLabel)
        {
            uint freed = cur.Frees;
            try { library.Core.DramaSetSpeaker(bookId, cur.Section, cur.Sentence, cur.Start, choice); }
            catch { }
            lastDecision = (cur.Section, cur.Sentence, cur.Start, displayLabel, freed);
            resolvedCount++;
            DramaSynthService.InvalidateCast(bookId);
            onChanged();
            RebuildCard();
        }

        void RebuildCard()
        {
            host.Children.Clear();
            activeLine = null;
            UnsureLine[] raw;
            try { raw = library.Core.DramaReview(bookId, currentSection); }
            catch { raw = []; }

            var ordered = raw
                .OrderByDescending(u => u.Section == currentSection)
                .ThenByDescending(u => u.Frees)
                .ToArray();

            if (lastDecision is var (lSec, lSent, lStart, lName, lFreed))
            {
                var undoRow = new Grid();
                undoRow.Children.Add(new TextBlock
                {
                    Text = lFreed > 1 ? $"✓ 已设为「{lName}」，同时顺带确认了 {lFreed} 处台词" : $"✓ 已设为「{lName}」",
                    FontSize = 11.5,
                    Foreground = ThemeHelper.Brush(p.Ok),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                var undoBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "撤销", Height = 24, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right };
                undoBtn.Click += (_, _) =>
                {
                    try { library.Core.DramaClearSpeaker(bookId, lSec, lSent, lStart); } catch { }
                    lastDecision = null;
                    DramaSynthService.InvalidateCast(bookId);
                    onChanged();
                    RebuildCard();
                };
                undoRow.Children.Add(undoBtn);
                host.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(8),
                    Background = ThemeHelper.Brush(Color.FromArgb(24, p.Ok.R, p.Ok.G, p.Ok.B)),
                    Padding = new Thickness(10, 6, 10, 6),
                    Child = undoRow,
                });
            }

            if (ordered.Length == 0 || skipOffset >= ordered.Length)
            {
                host.Children.Add(new TextBlock
                {
                    Text = resolvedCount > 0 ? $"🎉 全部处理完成（本轮已确认 {resolvedCount} 处）" : "🎉 读到的这里，所有台词都已确认说话人。",
                    FontSize = 13.5,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = ThemeHelper.Brush(p.T1),
                    Margin = new Thickness(0, 16, 0, 12),
                });
                return;
            }

            var cur = ordered[skipOffset];
            activeLine = cur;
            var metaRow = new Grid();
            metaRow.Children.Add(new TextBlock { Text = $"第 {cur.Section + 1} 章 · 待确认 {ordered.Length - skipOffset} 处", FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T3) });
            if (cur.Frees > 1)
                metaRow.Children.Add(new TextBlock { Text = $"定下这句可连带确定 {cur.Frees} 处", FontSize = 11, Foreground = ThemeHelper.Brush(tint.Accent), HorizontalAlignment = HorizontalAlignment.Right });
            host.Children.Add(metaRow);

            var quoteCard = new Border
            {
                CornerRadius = new CornerRadius(12),
                Background = ThemeHelper.Brush(p.Inset),
                Padding = new Thickness(14, 12, 14, 12),
            };
            var qs = new StackPanel { Spacing = 6 };
            if (!string.IsNullOrWhiteSpace(cur.Place))
                qs.Children.Add(new TextBlock { Text = $"场景：{cur.Place}", FontSize = 11, Foreground = ThemeHelper.Brush(p.T3) });
            var ctx = cur.Context ?? [];
            int relIdx = (int)cur.Sentence - (int)cur.ContextFirst;
            string beforeCtx = relIdx > 0 && relIdx <= ctx.Length ? string.Join(" ", ctx.Take(relIdx)) : "";
            string afterCtx = relIdx >= 0 && relIdx + 1 < ctx.Length ? string.Join(" ", ctx.Skip(relIdx + 1)) : "";
            if (!string.IsNullOrWhiteSpace(beforeCtx))
                qs.Children.Add(new TextBlock { Text = beforeCtx, FontSize = 12, Foreground = ThemeHelper.Brush(p.T3), TextWrapping = TextWrapping.Wrap });
            qs.Children.Add(new TextBlock { Text = cur.Text, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1), TextWrapping = TextWrapping.Wrap });
            if (!string.IsNullOrWhiteSpace(afterCtx))
                qs.Children.Add(new TextBlock { Text = afterCtx, FontSize = 12, Foreground = ThemeHelper.Brush(p.T3), TextWrapping = TextWrapping.Wrap });
            quoteCard.Child = qs;
            host.Children.Add(quoteCard);

            host.Children.Add(new TextBlock { Text = "这句是谁说的？（按 1 / 2 快捷键可秒选）", FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T2) });

            string[] candNames = (cur.Names is { Length: > 0 } ? cur.Names : cur.Present) ?? [];
            var candsPanel = new StackPanel { Spacing = 6 };
            for (int i = 0; i < candNames.Length; i++)
            {
                string cand = candNames[i];
                string pct = i < cur.Top.Length ? $" ({Math.Round(cur.Top[i].P * 100)}%)" : "";
                string badge = i == 0 ? "1 · 最可能" : $"{i + 1}";
                var cb = new Button
                {
                    Style = (Style)Application.Current.Resources[i == 0 ? "YYPrimaryPillButtonStyle" : "YYPillButtonStyle"],
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Height = 32,
                    Content = $"{badge}   {cand}{pct}",
                };
                cb.Click += (_, _) => CommitChoice(cur, new UserChoice.Person(cand), cand);
                candsPanel.Children.Add(cb);
            }
            host.Children.Add(candsPanel);

            var subActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var otherBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "别的人物…", FontSize = 11.5 };
            otherBtn.Click += (_, _) => { openPickerNext = true; dlgRef?.Hide(); };
            var notSpeechBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "不是台词（旁白）", FontSize = 11.5 };
            notSpeechBtn.Click += (_, _) => CommitChoice(cur, new UserChoice.NotSpeech(), "旁白");
            var groupBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "众人齐声", FontSize = 11.5 };
            groupBtn.Click += (_, _) => CommitChoice(cur, new UserChoice.Group("众人"), "众人");
            var skipBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "跳过 →", FontSize = 11.5 };
            skipBtn.Click += (_, _) => { skipOffset++; RebuildCard(); };
            subActions.Children.Add(otherBtn);
            subActions.Children.Add(notSpeechBtn);
            subActions.Children.Add(groupBtn);
            subActions.Children.Add(skipBtn);
            host.Children.Add(subActions);
        }

        host.KeyDown += (_, e) =>
        {
            if (activeLine is null) return;
            string[] names = (activeLine.Names is { Length: > 0 } ? activeLine.Names : activeLine.Present) ?? [];
            if (e.Key == Windows.System.VirtualKey.Number1 && names.Length >= 1)
            {
                e.Handled = true;
                CommitChoice(activeLine, new UserChoice.Person(names[0]), names[0]);
            }
            else if (e.Key == Windows.System.VirtualKey.Number2 && names.Length >= 2)
            {
                e.Handled = true;
                CommitChoice(activeLine, new UserChoice.Person(names[1]), names[1]);
            }
        };

        while (true)
        {
            openPickerNext = false;
            RebuildCard();
            dlgRef = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = "确认台词说话人",
                Content = host,
                CloseButtonText = "完成",
            };
            await dlgRef.ShowAsync();

            if (openPickerNext && activeLine is not null)
            {
                var chosen = await ShowPeoplePickerDialogAsync(xamlRoot, library, bookId, currentSection, "选择这句的说话人");
                if (chosen is not null)
                {
                    string lbl = chosen switch
                    {
                        UserChoice.Person pr => pr.Name,
                        UserChoice.Group gr => gr.Name,
                        _ => "旁白",
                    };
                    CommitChoice(activeLine, chosen, lbl);
                }
                continue;
            }
            break;
        }
    }

    // ---------- 5. Role Detail Editor (1:1 CastView.swift RoleDetail: rate, pitch, audition, auto-cast reset, alias fix) ----------

    public static async Task ShowRoleDetailDialogAsync(
        Window ownerWindow, XamlRoot xamlRoot, LibraryService library, string bookId, string roleName,
        Func<string, string?, Action<string>, Task> openVoicePicker, Action onChanged)
    {
        bool dark = ThemeHelper.IsDark(xamlRoot.Content is FrameworkElement fe ? fe.ActualTheme : ElementTheme.Light);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(library.Prefs.Tint, dark);

        string currentRole = roleName;
        var host = new StackPanel { Width = 400, Spacing = 12 };
        string? pendingAction = null;
        ContentDialog? roleDlg = null;

        void RebuildRole()
        {
            host.Children.Clear();
            string[] aliases;
            CharacterGender gender;
            RoleVoice? curRoleVoice = null;
            try
            {
                aliases = library.Core.DramaNames(bookId, currentRole);
                gender = library.Core.DramaGender(bookId, currentRole);
                curRoleVoice = library.Core.BookRoles(bookId).FirstOrDefault(r => r.Name == currentRole)?.Voice;
            }
            catch
            {
                aliases = [currentRole];
                gender = CharacterGender.Unknown;
            }
            if (aliases.Length == 0) aliases = [currentRole];

            // Gender picker (男 / 女 / 不确定)
            var genderRow = new Grid { ColumnSpacing = 6 };
            (CharacterGender G, string L)[] gOpts = [(CharacterGender.M, "男"), (CharacterGender.F, "女"), (CharacterGender.Unknown, "不确定")];
            for (int i = 0; i < gOpts.Length; i++)
            {
                genderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var (gv, gl) = gOpts[i];
                var gb = new Button
                {
                    Style = (Style)Application.Current.Resources[gender == gv ? "YYPrimaryPillButtonStyle" : "YYPillButtonStyle"],
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Content = gl,
                    Height = 26,
                    FontSize = 11.5,
                };
                gb.Click += (_, _) =>
                {
                    try { library.Core.DramaEditCast(bookId, new CastEdit.SetGender(currentRole, gv)); } catch { }
                    DramaSynthService.InvalidateCast(bookId);
                    onChanged();
                    RebuildRole();
                };
                Grid.SetColumn(gb, i);
                genderRow.Children.Add(gb);
            }
            host.Children.Add(new TextBlock { Text = "角色性别（用于自动匹配声线）", FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T3) });
            host.Children.Add(genderRow);

            // Aliases chips (fixes stale roleName bug after CastEdit.Show!)
            host.Children.Add(new TextBlock { Text = "书里对他的称呼（点击设为主名，× 拆回独立人物）", FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T3) });
            var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            for (int i = 0; i < aliases.Length; i++)
            {
                string al = aliases[i];
                bool primary = i == 0;
                var chipSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                var mainBtn = new Button
                {
                    Content = al,
                    Padding = new Thickness(6, 2, 6, 2),
                    FontSize = 11.5,
                    Background = new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    FontWeight = primary ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                };
                mainBtn.Click += (_, _) =>
                {
                    if (!primary)
                    {
                        try
                        {
                            library.Core.DramaEditCast(bookId, new CastEdit.Show(al));
                            currentRole = al;
                            if (roleDlg is not null) roleDlg.Title = $"人物设定 · {currentRole}";
                            DramaSynthService.InvalidateCast(bookId);
                            onChanged();
                            RebuildRole();
                        }
                        catch { }
                    }
                };
                chipSp.Children.Add(mainBtn);
                if (!primary)
                {
                    var splitBtn = new Button { Content = "×", Padding = new Thickness(3, 0, 3, 0), FontSize = 11, Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0) };
                    splitBtn.Click += (_, _) =>
                    {
                        try
                        {
                            library.Core.DramaEditCast(bookId, new CastEdit.Split(al));
                            DramaSynthService.InvalidateCast(bookId);
                            onChanged();
                            RebuildRole();
                        }
                        catch { }
                    };
                    chipSp.Children.Add(splitBtn);
                }
                chips.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(7),
                    Background = ThemeHelper.Brush(primary ? tint.Soft : p.Inset),
                    Child = chipSp,
                });
            }
            host.Children.Add(new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = chips });

            var addAliasBox = new TextBox { PlaceholderText = "＋ 添加书中其他别名（输入回车合并）…", FontSize = 12 };
            addAliasBox.KeyDown += (_, e) =>
            {
                if (e.Key == Windows.System.VirtualKey.Enter && !string.IsNullOrWhiteSpace(addAliasBox.Text))
                {
                    try
                    {
                        library.Core.DramaEditCast(bookId, new CastEdit.AddName(currentRole, addAliasBox.Text.Trim()));
                        DramaSynthService.InvalidateCast(bookId);
                        onChanged();
                        RebuildRole();
                    }
                    catch { }
                }
            };
            host.Children.Add(addAliasBox);

            // Voice buttons + audition + reset to auto-cast
            double curRate = curRoleVoice?.Rate ?? 1.0;
            double curPitch = curRoleVoice?.Pitch ?? 0.0;
            bool curRight = curRoleVoice?.Right ?? false;
            string? curVid = DramaSynthService.NormalizeVoiceId(curRoleVoice?.Voice);

            var voiceActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var pickVBtn = new Button { Style = (Style)Application.Current.Resources["YYPrimaryPillButtonStyle"], Content = "选用音色…", FontSize = 11.5 };
            pickVBtn.Click += (_, _) => { pendingAction = "pick"; roleDlg?.Hide(); };
            var cloneVBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "🎙 录声音…", FontSize = 11.5 };
            cloneVBtn.Click += (_, _) => { pendingAction = "clone"; roleDlg?.Hide(); };
            var auditionBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "▶ 试听", FontSize = 11.5 };
            auditionBtn.Click += (_, _) =>
            {
                string? sampleVoice = curVid;
                if (string.IsNullOrEmpty(sampleVoice))
                {
                    try { sampleVoice = library.Core.DramaRoleVoices(bookId).FirstOrDefault(v => v.Name == currentRole)?.Voice; } catch { }
                }
                _ = AudioPlayer.PreviewVoiceAsync(library, sampleVoice, library.Prefs.SpeechRate * curRate, $"你好，我是{currentRole}。");
            };
            voiceActions.Children.Add(pickVBtn);
            voiceActions.Children.Add(cloneVBtn);
            voiceActions.Children.Add(auditionBtn);
            host.Children.Add(voiceActions);

            if (!string.IsNullOrEmpty(curVid) || Math.Abs(curRate - 1.0) > 0.02 || Math.Abs(curPitch) > 0.05)
            {
                var resetBtn = new Button
                {
                    Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
                    Content = "恢复自动选角（清除专属音色与微调）",
                    FontSize = 11,
                    Height = 24,
                };
                resetBtn.Click += (_, _) =>
                {
                    try
                    {
                        library.Core.SetBookRole(bookId, currentRole, new RoleVoice(null, 1.0, 0.0, curRight));
                        DramaSynthService.InvalidateCast(bookId);
                        onChanged();
                        RebuildRole();
                    }
                    catch { }
                };
                host.Children.Add(resetBtn);
            }

            var sideRow = new Grid();
            sideRow.Children.Add(new TextBlock { Text = "气泡靠右显示", FontSize = 12, Foreground = ThemeHelper.Brush(p.T1), VerticalAlignment = VerticalAlignment.Center });
            var rightSw = new ToggleSwitch { IsOn = curRight, HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 0, OnContent = "", OffContent = "" };
            rightSw.Toggled += (_, _) =>
            {
                curRight = rightSw.IsOn;
                try { library.Core.SetBookRole(bookId, currentRole, new RoleVoice(curVid, curRate, curPitch, curRight)); onChanged(); } catch { }
            };
            sideRow.Children.Add(rightSw);
            host.Children.Add(sideRow);

            var rateSlider = new Slider { Header = $"角色语速微调（{curRate:0.00}×）", Minimum = 0.7, Maximum = 1.4, StepFrequency = 0.05, Value = Math.Clamp(curRate, 0.7, 1.4) };
            rateSlider.ValueChanged += (_, e) =>
            {
                curRate = Math.Round(e.NewValue, 2);
                rateSlider.Header = $"角色语速微调（{curRate:0.00}×）";
                try { library.Core.SetBookRole(bookId, currentRole, new RoleVoice(curVid, curRate, curPitch, curRight)); onChanged(); } catch { }
            };
            host.Children.Add(rateSlider);

            var pitchSlider = new Slider { Header = $"音高微调（{curPitch:+0.0;-0.0;0.0} 半音）", Minimum = -6.0, Maximum = 6.0, StepFrequency = 0.5, Value = Math.Clamp(curPitch, -6.0, 6.0) };
            pitchSlider.ValueChanged += (_, e) =>
            {
                curPitch = Math.Round(e.NewValue, 1);
                pitchSlider.Header = $"音高微调（{curPitch:+0.0;-0.0;0.0} 半音）";
                try { library.Core.SetBookRole(bookId, currentRole, new RoleVoice(curVid, curRate, curPitch, curRight)); onChanged(); } catch { }
            };
            host.Children.Add(pitchSlider);
        }

        while (true)
        {
            pendingAction = null;
            RebuildRole();
            roleDlg = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = $"人物设定 · {currentRole}",
                Content = new ScrollViewer { MaxHeight = 460, Content = host },
                CloseButtonText = "完成",
            };
            await roleDlg.ShowAsync();

            if (pendingAction == "pick")
            {
                var latestRv = library.Core.BookRoles(bookId).FirstOrDefault(r => r.Name == currentRole)?.Voice;
                await openVoicePicker($"{currentRole}的声音", DramaSynthService.NormalizeVoiceId(latestRv?.Voice), vid =>
                {
                    try
                    {
                        library.Core.SetBookRole(bookId, currentRole, new RoleVoice(
                            DramaSynthService.NormalizeVoiceId(vid),
                            latestRv?.Rate ?? 1.0,
                            latestRv?.Pitch ?? 0.0,
                            latestRv?.Right ?? false));
                        DramaSynthService.InvalidateCast(bookId);
                    }
                    catch { }
                    onChanged();
                });
                continue;
            }
            if (pendingAction == "clone")
            {
                await ShowCloneVoiceDialogAsync(ownerWindow, xamlRoot, library, bookId, currentRole);
                onChanged();
                continue;
            }
            break;
        }
    }
}
