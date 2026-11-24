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
using Windows.System;
using Windows.UI;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
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

    private static readonly object ClonePreviewGate = new();
    private static readonly object ClonePreviewClaimant = new();
    private static CancellationTokenSource? ActiveClonePreviewCts;
    private static MediaPlayer? ActiveClonePreviewPlayer;

    public static void StopClonePreview()
    {
        CancellationTokenSource? cts;
        MediaPlayer? mp;
        lock (ClonePreviewGate)
        {
            cts = ActiveClonePreviewCts;
            mp = ActiveClonePreviewPlayer;
            ActiveClonePreviewCts = null;
            ActiveClonePreviewPlayer = null;
        }
        try { cts?.Cancel(); } catch { }
        try { mp?.Pause(); } catch { }
    }

    // ---------- 1. Voice Cloning Sheet (1:1 CloneVoiceView.swift: 348px progressive steps) ----------

    public static async Task<VoiceCard?> ShowCloneVoiceDialogAsync(
        Window ownerWindow, XamlRoot xamlRoot, LibraryService library, string? forBookId = null, string? forRoleName = null, string? existingCardId = null)
    {
        bool dark = ThemeHelper.IsDark(xamlRoot.Content is FrameworkElement fe ? fe.ActualTheme : ElementTheme.Light);
        var p = ThemeHelper.Palette(dark);
        var useTint = library.Prefs.Tint;
        var tint = ThemeHelper.TintColors(useTint, dark);
        var kami = dark ? YYPapers.Kaminight : YYPapers.Kami;
        var serifFont = new FontFamily(YYFontFamily.Serif.WindowsFamily());

        VoiceCard? existingCard = !string.IsNullOrWhiteSpace(existingCardId)
            ? library.LiveVoice.GetClonedCards().FirstOrDefault(c => c.Id == existingCardId)
            : null;

        string? bookTitle = null;
        string bookLang = "zh-Hans";
        if (!string.IsNullOrEmpty(forBookId))
        {
            try
            {
                var det = library.GetBookDetail(forBookId);
                bookTitle = det.Summary.Title;
                if (!string.IsNullOrWhiteSpace(det.Summary.Language))
                    bookLang = det.Summary.Language;
            }
            catch { }
        }

        string lang = existingCard is not null
            ? (existingCard.Lang.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "en" : "zh")
            : (bookLang.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "en" : "zh");
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

        string LoadNextBookSentence(string l)
        {
            if (!string.IsNullOrEmpty(forBookId))
            {
                try
                {
                    var prog = library.GetProgress(forBookId);
                    uint sec = prog?.Position.Section ?? 0;
                    int nextSent = (int)(prog?.Position.Sentence ?? 0) + 1;
                    var all = library.GetSpokenSentences(forBookId, sec);
                    if (nextSent >= 0 && nextSent < all.Count && all[nextSent].Trim().Length >= 6)
                        return all[nextSent].Trim();
                    var good = all.FirstOrDefault(s => s.Trim().Length >= 8);
                    if (!string.IsNullOrEmpty(good))
                        return good.Trim();
                }
                catch { }
            }
            return l == "en"
                ? "The lamp on the desk was still warm, and outside the rain had just stopped."
                : "窗外的雨不知什么时候停了，屋里只剩下翻书的轻响。";
        }

        string promptScript = LoadScript(lang);
        string transcriptText = promptScript;
        bool consented = existingCard is not null;
        bool importedFile = false;
        string step = "record"; // record | checking | fault | ready | use
        float[]? capturedSamples24k = null;
        CardCheck? latestCheck = null;
        VoiceCard? savedCard = null;
        string cardName = existingCard is not null
            ? existingCard.Name
            : (!string.IsNullOrWhiteSpace(forRoleName) ? forRoleName! : (lang == "en" ? "My Voice" : "我的声音"));
        bool syncElevenLabs = existingCard is not null && library.Prefs.ClonedUseElevenLabs.Contains(existingCard.Id);
        bool previewSynthesizing = false;
        bool previewSounding = false;
        bool savingCard = false;
        string? inlineError = null;

        MediaCapture? capture = null;
        InMemoryRandomAccessStream? recStream = null;
        bool isRecording = false;
        bool stoppingRecording = false;
        bool samplingMeter = false;
        DateTime recStartUtc = DateTime.UtcNow;
        double speechActiveSeconds = 0;
        double silenceSeconds = 0;

        const int BarCount = 28;
        var barLevels = new double[BarCount];
        var barBorders = new Border[BarCount];
        var speakingBarBorders = new Border[3];

        ContentDialog? dlgRef = null;
        var outerRoot = new StackPanel { Spacing = 12, Padding = new Thickness(18, 16, 18, 16) };
        TextBlock? recHintLabel = null;

        var recTimer = new DispatcherTimer { Interval = System.TimeSpan.FromMilliseconds(80) };
        var speakingAnimTimer = new DispatcherTimer { Interval = System.TimeSpan.FromMilliseconds(140) };
        int speakingTick = 0;

        speakingAnimTimer.Tick += (_, _) =>
        {
            speakingTick++;
            double[] heights = previewSounding
                ? [5 + (speakingTick % 3) * 3, 12 - (speakingTick % 2) * 5, 6 + ((speakingTick + 1) % 3) * 2.5]
                : [4.0, 4.0, 4.0];
            for (int i = 0; i < 3; i++)
            {
                if (speakingBarBorders[i] is not null)
                    speakingBarBorders[i].Height = heights[i];
            }
        };

        async Task StopRecordingAndCheckAsync()
        {
            if (!isRecording || stoppingRecording) return;
            stoppingRecording = true;
            isRecording = false;
            recTimer.Stop();

            byte[]? wavBytes = null;
            try
            {
                if (capture is not null) await capture.StopRecordAsync();
                if (recStream is not null && recStream.Size > 44)
                {
                    recStream.Seek(0);
                    wavBytes = new byte[recStream.Size];
                    await recStream.ReadAsync(wavBytes.AsBuffer(), (uint)wavBytes.Length, InputStreamOptions.None);
                }
            }
            catch { }
            finally
            {
                try { recStream?.Dispose(); } catch { }
                recStream = null;
                try { capture?.Dispose(); } catch { }
                capture = null;
                stoppingRecording = false;
            }

            if (wavBytes is null || wavBytes.Length <= 1024)
            {
                Array.Clear(barLevels);
                RebuildSheet();
                return;
            }

            string tmpWav = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"yuyue-clone-rec-{Guid.NewGuid():N}.wav");
            try
            {
                await File.WriteAllBytesAsync(tmpWav, wavBytes);
                await EvaluateAudioFileAsync(tmpWav, isImported: false);
            }
            finally
            {
                try { File.Delete(tmpWav); } catch { }
            }
        }

        recTimer.Tick += async (_, _) =>
        {
            if (!isRecording || recStream is null || samplingMeter) return;
            samplingMeter = true;
            double level;
            double dbFs;
            try
            {
                (level, dbFs) = await SampleLatestRmsLevelAsync(recStream);
            }
            finally
            {
                samplingMeter = false;
            }
            if (!isRecording || recStream is null) return;

            double elapsed = (DateTime.UtcNow - recStartUtc).TotalSeconds;

            Array.Copy(barLevels, 1, barLevels, 0, BarCount - 1);
            barLevels[BarCount - 1] = level;
            for (int i = 0; i < BarCount; i++)
            {
                if (barBorders[i] is not null)
                    barBorders[i].Height = Math.Clamp(4.0 + barLevels[i] * 18.0, 4.0, 22.0);
            }

            if (recHintLabel is not null)
            {
                int secInt = (int)elapsed;
                recHintLabel.Text = L10n.T($"正在录音 {secInt / 60}:{secInt % 60:D2} · 念完停顿 1.2 秒自动停");
            }

            if (dbFs > -38.0)
            {
                speechActiveSeconds += 0.08;
                silenceSeconds = 0;
            }
            else if (speechActiveSeconds >= 0.45 && elapsed >= 2.2)
            {
                silenceSeconds += 0.08;
                if (silenceSeconds >= 1.2)
                {
                    await StopRecordingAndCheckAsync();
                    return;
                }
            }

            if (elapsed >= 24.0)
            {
                await StopRecordingAndCheckAsync();
            }
        };

        async Task PlayAuditionPreviewAsync()
        {
            if (capturedSamples24k is null || previewSynthesizing) return;
            previewSynthesizing = true;
            previewSounding = false;
            RebuildSheet();

            try
            {
                AudioPlayer.StopActivePreview();
                StopClonePreview();

                string sampleLine = LoadNextBookSentence(lang);
                string refText = string.IsNullOrWhiteSpace(transcriptText) ? promptScript : transcriptText.Trim();
                byte[]? wav = null;
                if (library.LiveVoice.IsReady && CardVoiceEngine.SpeaksLanguage(lang))
                {
                    wav = await CardVoiceEngine.Instance.PreviewReferenceWavAsync(
                        library.Core, library.LiveVoice, capturedSamples24k, 24_000, refText, sampleLine, lang);
                }
                if (wav is not { Length: > 44 })
                {
                    // Fallback: play back the clean recorded reference WAV so user can still verify audio clarity
                    string tmpPlay = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"yuyue-clone-raw-{Guid.NewGuid():N}.wav");
                    try
                    {
                        library.Core.CardWriteWav(tmpPlay, capturedSamples24k, 24_000);
                        if (File.Exists(tmpPlay)) wav = await File.ReadAllBytesAsync(tmpPlay);
                    }
                    finally { try { File.Delete(tmpPlay); } catch { } }
                }

                previewSynthesizing = false;
                if (wav is { Length: > 44 } && dlgRef is not null)
                {
                    previewSounding = true;
                    speakingAnimTimer.Start();
                    RebuildSheet();
                    await PlayWavBytesOnceAsync(wav);
                }
            }
            catch { }
            finally
            {
                previewSynthesizing = false;
                previewSounding = false;
                speakingAnimTimer.Stop();
                if (step == "ready") RebuildSheet();
            }
        }

        async Task EvaluateAudioFileAsync(string audioPath, bool isImported)
        {
            importedFile = isImported;
            step = "checking";
            latestCheck = null;
            RebuildSheet();

            try
            {
                var samples = await Task.Run(() => library.Core.CardDecodeClip(audioPath, 24_000));
                capturedSamples24k = samples;
                string? heard = await WindowsTranscriber.TranscribeAsync(library, audioPath, lang);

                if (isImported)
                {
                    transcriptText = (heard ?? "").Trim();
                    if (string.IsNullOrEmpty(transcriptText))
                    {
                        float approxSecs = samples.Length / 24_000f;
                        latestCheck = new CardCheck(
                            Ok: false,
                            Seconds: approxSecs,
                            SpeechDb: -20f,
                            SnrDb: 25f,
                            ClippedRatio: 0f,
                            Cer: null,
                            Faults: [],
                            Messages: ["请先在上方填上这段录音里说的原话，再点“改好文字再检一次”"]);
                        step = "fault";
                        RebuildSheet();
                        return;
                    }
                }
                else
                {
                    transcriptText = promptScript;
                }

                var chk = await Task.Run(() => library.Core.CardCheck(
                    samples,
                    24_000,
                    transcriptText,
                    isImported ? null : (string.IsNullOrWhiteSpace(heard) ? null : heard.Trim())));
                latestCheck = chk;
                step = chk.Ok ? "ready" : "fault";
                RebuildSheet();

                if (chk.Ok)
                    _ = PlayAuditionPreviewAsync();
            }
            catch
            {
                capturedSamples24k = [];
                latestCheck = new CardCheck(
                    Ok: false,
                    Seconds: 0f,
                    SpeechDb: -60f,
                    SnrDb: 0f,
                    ClippedRatio: 0f,
                    Cer: 1f,
                    Faults: [CardFault.TooShort],
                    Messages: ["没读出这段音频，换一段常见的 m4a 或 wav 试试"]);
                step = "fault";
                RebuildSheet();
            }
        }

        void ResetToRecord()
        {
            StopClonePreview();
            AudioPlayer.StopActivePreview();
            speakingAnimTimer.Stop();
            previewSynthesizing = false;
            previewSounding = false;
            capturedSamples24k = null;
            latestCheck = null;
            importedFile = false;
            inlineError = null;
            transcriptText = promptScript;
            Array.Clear(barLevels);
            step = "record";
            RebuildSheet();
        }

        async Task SaveVoiceCardNowAsync()
        {
            if (savingCard || capturedSamples24k is null || capturedSamples24k.Length < 12_000) return;
            string cleanName = cardName.Trim();
            if (string.IsNullOrEmpty(cleanName)) return;
            string cleanTranscript = string.IsNullOrWhiteSpace(transcriptText) ? promptScript : transcriptText.Trim();

            inlineError = null;
            savingCard = true;
            StopClonePreview();
            RebuildSheet();

            var latestExisting = !string.IsNullOrWhiteSpace(existingCardId)
                ? library.LiveVoice.GetClonedCards().FirstOrDefault(c => c.Id == existingCardId) ?? existingCard
                : null;
            string targetCardId = latestExisting?.Id ?? $"cloned.{Guid.NewGuid():D}";
            string stageDir = System.IO.Path.Combine(library.DataDir, "clones", targetCardId);
            Directory.CreateDirectory(stageDir);
            string wavFile = System.IO.Path.Combine(stageDir, "neutral.wav");

            try
            {
                float secs = library.Core.CardWriteWav(wavFile, capturedSamples24k, 24_000);
                CharacterGender gender = latestExisting?.Gender ?? CharacterGender.Unknown;
                try { gender = library.Core.SpeakerVoiceGender(capturedSamples24k, 24_000); } catch { }
                float avgRate = Math.Max(2.5f, cleanTranscript.Length / Math.Max(1.0f, secs));

                var neutralClip = new CardClip(CardStyle.Neutral, "neutral.wav", cleanTranscript, secs);
                CardClip[] mergedClips = latestExisting is not null
                    ? new[] { neutralClip }.Concat(latestExisting.Clips.Where(c => c.Style != CardStyle.Neutral)).ToArray()
                    : [neutralClip];

                var draft = new VoiceCard(
                    Id: targetCardId,
                    Name: cleanName,
                    Gender: gender,
                    Age: latestExisting?.Age ?? CharacterAge.Young,
                    Tags: latestExisting?.Tags is { Length: > 0 } ? latestExisting.Tags : ["克隆"],
                    Lang: lang,
                    Origin: new CardOrigin.Cloned(),
                    Clips: mergedClips,
                    Description: latestExisting?.Description ?? "本机克隆声音",
                    Version: (latestExisting?.Version ?? 0u) + 1u,
                    Narrator: latestExisting?.Narrator ?? true,
                    Rate: avgRate,
                    Embedding: [],
                    Sync: latestExisting?.Sync ?? false);

                savedCard = await Task.Run(() => library.Core.CardSave(null, draft, stageDir));

                if (syncElevenLabs && library.CloudVoices.IsConfigured(CloudTtsProvider.ElevenLabs))
                {
                    string? elId = await UploadElevenLabsCloneAsync(library, cleanName, wavFile);
                    if (!string.IsNullOrEmpty(elId))
                    {
                        library.Prefs.ClonedElevenLabsVoiceIds[savedCard.Id] = elId;
                        library.Prefs.ClonedUseElevenLabs.Add(savedCard.Id);
                        library.SavePrefs();
                    }
                }

                DramaSynthService.InvalidateAllCasts();
                App.CurrentApp.BroadcastVoiceChanged();
                savingCard = false;
                if (latestExisting is not null)
                {
                    dlgRef?.Hide();
                    return;
                }
                step = "use";
                RebuildSheet();
            }
            catch (Exception ex)
            {
                savingCard = false;
                inlineError = $"{L10n.T("没存下")}：{ex.Message}";
                RebuildSheet();
            }
        }

        Border MakeChoiceCard(string iconName, string titleText, string subText, bool highlight, Action onTap)
        {
            var g = new Grid { ColumnSpacing = 10 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var iconCircle = new Border
            {
                Width = 30,
                Height = 30,
                CornerRadius = new CornerRadius(15),
                Background = ThemeHelper.Brush(highlight ? tint.Soft : p.Inset),
                Child = YYIcons.Make(iconName, 13.5, ThemeHelper.Brush(highlight ? tint.Accent : p.T2)),
                VerticalAlignment = VerticalAlignment.Center,
            };
            g.Children.Add(iconCircle);

            var txtStack = new StackPanel { Spacing = 1.5, VerticalAlignment = VerticalAlignment.Center };
            txtStack.Children.Add(L10n.Skip(new TextBlock
            {
                Text = L10n.T(titleText),
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = ThemeHelper.Brush(highlight ? tint.Accent : p.T1),
                TextTrimming = TextTrimming.CharacterEllipsis,
            }));
            txtStack.Children.Add(new TextBlock
            {
                Text = L10n.T(subText),
                FontSize = 10.5,
                Foreground = ThemeHelper.Brush(p.T3),
                TextWrapping = TextWrapping.Wrap,
            });
            Grid.SetColumn(txtStack, 1);
            g.Children.Add(txtStack);

            var chev = YYIcons.Make("chevron-right", 11, ThemeHelper.Brush(p.T3));
            chev.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(chev, 2);
            g.Children.Add(chev);

            var btn = new Button
            {
                Content = g,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(12, 10, 12, 10),
                CornerRadius = new CornerRadius(12),
                Background = ThemeHelper.Brush(highlight ? tint.Soft : p.Card),
                BorderBrush = ThemeHelper.Brush(highlight ? tint.Edge : p.Line),
                BorderThickness = new Thickness(0.75),
                UseSystemFocusVisuals = false,
            };
            btn.Click += (_, _) => onTap();
            return new Border { Child = btn };
        }

        void RebuildSheet()
        {
            outerRoot.Children.Clear();
            recHintLabel = null;

            // Header row
            var hdr = new Grid { ColumnSpacing = 8, MinHeight = 26 };
            hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            string hdrTitle = step switch
            {
                "ready" => "听听像不像",
                "use" => "存好了",
                _ => existingCard is not null ? "重新录一段" : "克隆一个声音",
            };
            hdr.Children.Add(new TextBlock
            {
                Text = L10n.T(hdrTitle),
                FontSize = 15.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = ThemeHelper.Brush(p.T1),
                VerticalAlignment = VerticalAlignment.Center,
            });

            if (step == "record" && !isRecording)
            {
                var langSeg = YYChrome.MakeSegmentedBar(
                    [("zh", "中文"), ("en", "EN")],
                    lang,
                    nextLang =>
                    {
                        lang = nextLang;
                        promptScript = LoadScript(lang);
                        transcriptText = promptScript;
                        if (cardName is "我的声音" or "My Voice")
                            cardName = lang == "en" ? "My Voice" : "我的声音";
                        RebuildSheet();
                    },
                    dark,
                    fontSize: 11,
                    height: 25);
                Grid.SetColumn(langSeg, 1);
                hdr.Children.Add(langSeg);
            }

            if (step == "ready" && latestCheck is { Ok: true } passChk)
            {
                var okBadge = new TextBlock
                {
                    Text = $"✓ {L10n.T("录音合格")} · {passChk.Seconds:0.1} {L10n.T("秒")}",
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = ThemeHelper.Brush(tint.Accent),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(okBadge, 2);
                hdr.Children.Add(okBadge);
            }
            else
            {
                var closeTxtBtn = new Button
                {
                    Content = L10n.T(step == "use" ? "完成" : "取消"),
                    Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
                    Foreground = ThemeHelper.Brush(p.T3),
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                closeTxtBtn.Click += (_, _) => dlgRef?.Hide();
                Grid.SetColumn(closeTxtBtn, 2);
                hdr.Children.Add(closeTxtBtn);
            }
            outerRoot.Children.Add(hdr);

            if (step == "record")
            {
                // Consent card
                var consentRow = new Grid { ColumnSpacing = 9 };
                consentRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                consentRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var chkBox = new Border
                {
                    Width = 16,
                    Height = 16,
                    CornerRadius = new CornerRadius(5),
                    Margin = new Thickness(0, 2, 0, 0),
                    VerticalAlignment = VerticalAlignment.Top,
                    Background = ThemeHelper.Brush(consented ? tint.Accent : p.Card),
                    BorderBrush = ThemeHelper.Brush(consented ? tint.Accent : p.T3),
                    BorderThickness = new Thickness(consented ? 0 : 1.2),
                    Child = consented ? YYIcons.Make("checkmark", 9.5, ThemeHelper.Brush(Colors.White)) : null,
                };
                consentRow.Children.Add(chkBox);

                var cTextCol = new StackPanel { Spacing = 2 };
                cTextCol.Children.Add(new TextBlock
                {
                    Text = L10n.T("这是我的声音，或者我得到了本人同意"),
                    FontSize = 11.5,
                    Foreground = ThemeHelper.Brush(p.T1),
                    TextWrapping = TextWrapping.Wrap,
                });
                if (!consented)
                {
                    cTextCol.Children.Add(new TextBlock
                    {
                        Text = L10n.T("先勾上这一项才能开始录音或导入音频"),
                        FontSize = 10,
                        Foreground = ThemeHelper.Brush(p.T3),
                        TextWrapping = TextWrapping.Wrap,
                    });
                }
                Grid.SetColumn(cTextCol, 1);
                consentRow.Children.Add(cTextCol);

                var consentBtn = new Button
                {
                    Content = consentRow,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Padding = new Thickness(11, 9, 11, 9),
                    CornerRadius = new CornerRadius(11),
                    Background = ThemeHelper.Brush(p.Card),
                    BorderBrush = ThemeHelper.Brush(p.Line),
                    BorderThickness = new Thickness(0.5),
                    UseSystemFocusVisuals = false,
                };
                consentBtn.Click += (_, _) =>
                {
                    if (isRecording) return;
                    consented = !consented;
                    RebuildSheet();
                };
                outerRoot.Children.Add(consentBtn);

                // Read-only Songti prompt card on warm Kami paper
                var promptStack = new StackPanel { Spacing = 6 };
                var promptTop = new Grid();
                promptTop.Children.Add(new TextBlock
                {
                    Text = L10n.T("请用平常自然的语气念这一句"),
                    FontSize = 10.5,
                    Foreground = ThemeHelper.Brush(p.T3),
                });
                promptTop.Children.Add(new TextBlock
                {
                    Text = L10n.T("约 8 秒"),
                    FontSize = 10.5,
                    Foreground = ThemeHelper.Brush(p.T3),
                    HorizontalAlignment = HorizontalAlignment.Right,
                });
                promptStack.Children.Add(promptTop);
                promptStack.Children.Add(L10n.Skip(new TextBlock
                {
                    Text = promptScript,
                    FontFamily = serifFont,
                    FontSize = 15,
                    LineHeight = 25,
                    Foreground = ThemeHelper.Brush(kami.Ink),
                    TextWrapping = TextWrapping.Wrap,
                }));

                outerRoot.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(13),
                    Background = ThemeHelper.Brush(kami.Bg),
                    BorderBrush = ThemeHelper.Brush(kami.Line),
                    BorderThickness = new Thickness(0.75),
                    Padding = new Thickness(14, 12, 14, 12),
                    Child = promptStack,
                });

                // Mic button + live 28-bar waveform + 1.2s silence auto-stop
                var recCenter = new StackPanel { Spacing = 9, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 2) };
                var micCircle = new Button
                {
                    Width = 54,
                    Height = 54,
                    MinWidth = 54,
                    MinHeight = 54,
                    Padding = new Thickness(0),
                    CornerRadius = new CornerRadius(27),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    IsEnabled = consented,
                    Opacity = consented ? 1.0 : 0.38,
                    Background = ThemeHelper.Brush(isRecording ? Color.FromArgb(24, p.Danger.R, p.Danger.G, p.Danger.B) : tint.Soft),
                    BorderBrush = ThemeHelper.Brush(isRecording ? Color.FromArgb(96, p.Danger.R, p.Danger.G, p.Danger.B) : tint.Edge),
                    BorderThickness = new Thickness(1),
                    UseSystemFocusVisuals = false,
                    Content = isRecording
                        ? (UIElement)new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(3), Background = ThemeHelper.Brush(p.Danger) }
                        : YYIcons.Make("mic", 20, ThemeHelper.Brush(tint.Accent)),
                };
                micCircle.Click += async (_, _) =>
                {
                    if (!consented) return;
                    if (isRecording)
                    {
                        await StopRecordingAndCheckAsync();
                    }
                    else
                    {
                        try
                        {
                            inlineError = null;
                            AudioPlayer.StopActivePreview();
                            StopClonePreview();
                            App.CurrentApp?.ClaimAudioPlayback(ClonePreviewClaimant, () => _ = StopRecordingAndCheckAsync());

                            capture = new MediaCapture();
                            await capture.InitializeAsync(new MediaCaptureInitializationSettings { StreamingCaptureMode = StreamingCaptureMode.Audio });
                            recStream = new InMemoryRandomAccessStream();
                            var profile = MediaEncodingProfile.CreateWav(AudioEncodingQuality.High);
                            profile.Audio.SampleRate = 24_000;
                            profile.Audio.ChannelCount = 1;
                            profile.Audio.BitsPerSample = 16;
                            await capture.StartRecordToStreamAsync(profile, recStream);

                            Array.Clear(barLevels);
                            speechActiveSeconds = 0;
                            silenceSeconds = 0;
                            isRecording = true;
                            recStartUtc = DateTime.UtcNow;
                            RebuildSheet();
                            recTimer.Start();
                        }
                        catch (Exception ex)
                        {
                            try { recStream?.Dispose(); } catch { }
                            recStream = null;
                            try { capture?.Dispose(); } catch { }
                            capture = null;
                            isRecording = false;
                            inlineError = $"{L10n.T("无法打开麦克风")}：{ex.Message}";
                            RebuildSheet();
                        }
                    }
                };
                recCenter.Children.Add(micCircle);

                var waveRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 3,
                    Height = 22,
                    HorizontalAlignment = HorizontalAlignment.Center,
                };
                for (int i = 0; i < BarCount; i++)
                {
                    var b = new Border
                    {
                        Width = 3,
                        Height = isRecording ? Math.Clamp(4.0 + barLevels[i] * 18.0, 4.0, 22.0) : 4.5,
                        CornerRadius = new CornerRadius(1.5),
                        VerticalAlignment = VerticalAlignment.Center,
                        Background = ThemeHelper.Brush(isRecording ? tint.Accent : p.Line),
                    };
                    barBorders[i] = b;
                    waveRow.Children.Add(b);
                }
                recCenter.Children.Add(waveRow);

                recHintLabel = new TextBlock
                {
                    Text = L10n.T(isRecording ? "正在录音 0:00 · 念完停顿 1.2 秒自动停" : "点一下开始录，念完停顿 1.2 秒自动停"),
                    FontSize = 11.5,
                    Foreground = ThemeHelper.Brush(p.T2),
                    HorizontalAlignment = HorizontalAlignment.Center,
                };
                recCenter.Children.Add(recHintLabel);
                outerRoot.Children.Add(recCenter);

                outerRoot.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line) });

                var bottomRow = new Grid();
                bottomRow.Children.Add(new TextBlock
                {
                    Text = L10n.T("不联网，录音只留在这台设备"),
                    FontSize = 10.5,
                    Foreground = ThemeHelper.Brush(p.T3),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                var importLink = new Button
                {
                    Content = L10n.T("或者选一段已有的录音"),
                    Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
                    Foreground = ThemeHelper.Brush(tint.Accent),
                    FontSize = 11,
                    IsEnabled = consented && !isRecording,
                    Opacity = consented && !isRecording ? 1.0 : 0.4,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                importLink.Click += async (_, _) =>
                {
                    if (!consented || isRecording) return;
                    string? filePath = await NativePickerHelper.PickSingleFileAsync(ownerWindow, [".wav", ".mp3", ".m4a", ".flac"]);
                    if (string.IsNullOrEmpty(filePath)) return;
                    if (cardName is "我的声音" or "My Voice")
                        cardName = System.IO.Path.GetFileNameWithoutExtension(filePath);
                    await EvaluateAudioFileAsync(filePath, isImported: true);
                };
                bottomRow.Children.Add(importLink);
                outerRoot.Children.Add(bottomRow);
            }
            else if (step == "checking")
            {
                var chkRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
                chkRow.Children.Add(new ProgressRing { Width = 16, Height = 16, IsActive = true });
                chkRow.Children.Add(new TextBlock
                {
                    Text = L10n.T("正在听清不清楚…"),
                    FontSize = 12.5,
                    Foreground = ThemeHelper.Brush(p.T2),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                outerRoot.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(12),
                    Background = ThemeHelper.Brush(p.Card),
                    BorderBrush = ThemeHelper.Brush(p.Line),
                    BorderThickness = new Thickness(0.75),
                    Padding = new Thickness(14, 14, 14, 14),
                    Child = chkRow,
                });
            }
            else if (step == "fault" && latestCheck is not null)
            {
                TextBox? impTranscriptBox = null;
                if (importedFile)
                {
                    outerRoot.Children.Add(new TextBlock
                    {
                        Text = L10n.T("录音里念的字"),
                        FontSize = 11,
                        Foreground = ThemeHelper.Brush(p.T3),
                    });
                    impTranscriptBox = new TextBox
                    {
                        Text = transcriptText,
                        PlaceholderText = L10n.T("把这段录音对应的原话填准，合成更稳"),
                        FontFamily = serifFont,
                        FontSize = 13.5,
                        AcceptsReturn = true,
                        TextWrapping = TextWrapping.Wrap,
                        CornerRadius = new CornerRadius(10),
                        Background = ThemeHelper.Brush(p.Card),
                    };
                    impTranscriptBox.TextChanged += (_, _) => transcriptText = impTranscriptBox.Text;
                    outerRoot.Children.Add(impTranscriptBox);
                }
                else
                {
                    outerRoot.Children.Add(new Border
                    {
                        CornerRadius = new CornerRadius(12),
                        Background = ThemeHelper.Brush(kami.Bg),
                        BorderBrush = ThemeHelper.Brush(kami.Line),
                        BorderThickness = new Thickness(0.5),
                        Padding = new Thickness(13, 10, 13, 10),
                        Child = L10n.Skip(new TextBlock
                        {
                            Text = promptScript,
                            FontFamily = serifFont,
                            FontSize = 14,
                            LineHeight = 23,
                            Foreground = ThemeHelper.Brush(kami.Ink),
                            TextWrapping = TextWrapping.Wrap,
                        }),
                    });
                }

                var faultStack = new StackPanel { Spacing = 4 };
                string headline = latestCheck.Messages.FirstOrDefault() ?? "这段录音不太清楚，再录一次试试";
                faultStack.Children.Add(new TextBlock
                {
                    Text = L10n.T(headline),
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = ThemeHelper.Brush(p.Danger),
                    TextWrapping = TextWrapping.Wrap,
                });
                foreach (string detail in FormatFaultLines(latestCheck))
                {
                    faultStack.Children.Add(new TextBlock
                    {
                        Text = detail,
                        FontSize = 11,
                        Foreground = ThemeHelper.Brush(p.T2),
                        TextWrapping = TextWrapping.Wrap,
                    });
                }
                outerRoot.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(12),
                    Background = ThemeHelper.Brush(Color.FromArgb(20, p.Danger.R, p.Danger.G, p.Danger.B)),
                    BorderBrush = ThemeHelper.Brush(Color.FromArgb(64, p.Danger.R, p.Danger.G, p.Danger.B)),
                    BorderThickness = new Thickness(0.75),
                    Padding = new Thickness(13, 11, 13, 11),
                    Child = faultStack,
                });

                var actRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
                if (importedFile)
                {
                    actRow.Children.Add(YYChrome.MakeSheetPillButton("改好文字再检一次", () =>
                    {
                        if (capturedSamples24k is null || string.IsNullOrWhiteSpace(transcriptText)) return;
                        var chk = library.Core.CardCheck(capturedSamples24k, 24_000, transcriptText.Trim(), null);
                        latestCheck = chk;
                        step = chk.Ok ? "ready" : "fault";
                        RebuildSheet();
                        if (chk.Ok) _ = PlayAuditionPreviewAsync();
                    }, dark, useTint, kind: "secondary", height: 30, horizontalPadding: 13));
                }
                actRow.Children.Add(YYChrome.MakeSheetPillButton("再录一次", ResetToRecord, dark, useTint, kind: "warm", height: 30, horizontalPadding: 14));
                outerRoot.Children.Add(actRow);
            }
            else if (step == "ready")
            {
                // Book-sentence audition card (1:1 CloneVoiceView.passReviewSection)
                bool hasLocalModel = library.LiveVoice.IsReady && CardVoiceEngine.SpeaksLanguage(lang);
                var prevContent = new StackPanel { Spacing = 6 };
                var prevTop = new Grid();
                string tryCaption = hasLocalModel
                    ? (!string.IsNullOrWhiteSpace(bookTitle)
                        ? $"正在用你的声音念《{bookTitle}》下一句"
                        : "正在用你的声音念下一句")
                    : L10n.T("回放刚才的参考录音");
                prevTop.Children.Add(L10n.Skip(new TextBlock
                {
                    Text = tryCaption,
                    FontSize = 10.5,
                    Foreground = ThemeHelper.Brush(p.T3),
                    MaxLines = 1,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                }));

                if (previewSynthesizing)
                {
                    prevTop.Children.Add(new ProgressRing { Width = 13, Height = 13, IsActive = true, HorizontalAlignment = HorizontalAlignment.Right });
                }
                else
                {
                    var bars = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2.5, Height = 13, HorizontalAlignment = HorizontalAlignment.Right };
                    for (int i = 0; i < 3; i++)
                    {
                        var sb = new Border
                        {
                            Width = 2.5,
                            Height = previewSounding ? (i == 1 ? 11 : 6) : 4,
                            CornerRadius = new CornerRadius(1.25),
                            VerticalAlignment = VerticalAlignment.Center,
                            Background = ThemeHelper.Brush(previewSounding ? tint.Accent : p.T3),
                        };
                        speakingBarBorders[i] = sb;
                        bars.Children.Add(sb);
                    }
                    prevTop.Children.Add(bars);
                }
                prevContent.Children.Add(prevTop);

                string previewBodyText = hasLocalModel
                    ? LoadNextBookSentence(lang)
                    : (string.IsNullOrWhiteSpace(transcriptText) ? promptScript : transcriptText.Trim());
                prevContent.Children.Add(L10n.Skip(new TextBlock
                {
                    Text = $"“{previewBodyText}”",
                    FontFamily = serifFont,
                    FontSize = 14,
                    LineHeight = 22,
                    MaxLines = 3,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = ThemeHelper.Brush(p.T1),
                }));

                var auditionCardBtn = new Button
                {
                    Content = prevContent,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Padding = new Thickness(13, 11, 13, 11),
                    CornerRadius = new CornerRadius(12),
                    Background = ThemeHelper.Brush(p.Card),
                    BorderBrush = ThemeHelper.Brush(p.Line),
                    BorderThickness = new Thickness(0.75),
                    IsEnabled = !previewSynthesizing,
                    UseSystemFocusVisuals = false,
                };
                ToolTipService.SetToolTip(auditionCardBtn, L10n.T("再听一遍"));
                auditionCardBtn.Click += async (_, _) => await PlayAuditionPreviewAsync();
                outerRoot.Children.Add(auditionCardBtn);

                if (!library.LiveVoice.IsReady)
                {
                    outerRoot.Children.Add(new Border
                    {
                        CornerRadius = new CornerRadius(10),
                        Background = ThemeHelper.Brush(p.Inset),
                        Padding = new Thickness(11, 8, 11, 8),
                        Child = new TextBlock
                        {
                            Text = L10n.T("需要下载本地语音克隆模型（约 160 MB）后，才能合成任意书页正文；当前试听为参考原声。"),
                            FontSize = 11,
                            Foreground = ThemeHelper.Brush(p.T2),
                            TextWrapping = TextWrapping.Wrap,
                        },
                    });
                }

                // Name input row
                Button? saveBtn = null;
                var nameRow = new Grid { ColumnSpacing = 8 };
                nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                nameRow.Children.Add(new TextBlock
                {
                    Text = L10n.T("名字"),
                    FontSize = 11.5,
                    Foreground = ThemeHelper.Brush(p.T3),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                var nameInput = L10n.Skip(new TextBox
                {
                    Text = cardName,
                    PlaceholderText = L10n.T("我的声音"),
                    BorderThickness = new Thickness(0),
                    Background = new SolidColorBrush(Colors.Transparent),
                    FontSize = 13,
                    FontWeight = FontWeights.Medium,
                    Padding = new Thickness(2, 4, 2, 4),
                });
                YYChrome.StripTextBoxChrome(nameInput);
                nameInput.TextChanged += (_, _) =>
                {
                    cardName = nameInput.Text;
                    if (saveBtn is not null)
                    {
                        bool canSave = !savingCard && !string.IsNullOrWhiteSpace(cardName);
                        saveBtn.IsEnabled = canSave;
                        saveBtn.Opacity = canSave ? 1.0 : 0.45;
                    }
                };
                Grid.SetColumn(nameInput, 1);
                nameRow.Children.Add(nameInput);

                outerRoot.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(10),
                    Background = ThemeHelper.Brush(p.Card),
                    BorderBrush = ThemeHelper.Brush(p.Line),
                    BorderThickness = new Thickness(0.75),
                    Padding = new Thickness(11, 4, 11, 4),
                    Child = nameRow,
                });

                if (library.CloudVoices.IsConfigured(CloudTtsProvider.ElevenLabs))
                {
                    var elRow = new Grid();
                    elRow.Children.Add(new TextBlock
                    {
                        Text = L10n.T("同步备份到 ElevenLabs 云端克隆"),
                        FontSize = 11.5,
                        Foreground = ThemeHelper.Brush(p.T2),
                        VerticalAlignment = VerticalAlignment.Center,
                    });
                    var elSw = new ToggleSwitch { IsOn = syncElevenLabs, HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 0, OnContent = "", OffContent = "" };
                    elSw.Toggled += (_, _) => syncElevenLabs = elSw.IsOn;
                    elRow.Children.Add(elSw);
                    outerRoot.Children.Add(elRow);
                }

                var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
                footer.Children.Add(YYChrome.MakeSheetPillButton("再录一次", ResetToRecord, dark, useTint, kind: "secondary", height: 32, horizontalPadding: 14));
                saveBtn = YYChrome.MakeSheetPillButton(
                    savingCard ? "保存中…" : (existingCard is not null ? "保存新录音" : "保存这个声音"),
                    () => _ = SaveVoiceCardNowAsync(),
                    dark, useTint, kind: "warm", height: 32, horizontalPadding: 16);
                bool canSaveInitial = !savingCard && !string.IsNullOrWhiteSpace(cardName);
                saveBtn.IsEnabled = canSaveInitial;
                saveBtn.Opacity = canSaveInitial ? 1.0 : 0.45;
                footer.Children.Add(saveBtn);
                outerRoot.Children.Add(footer);
            }
            else if (step == "use" && savedCard is not null)
            {
                outerRoot.Children.Add(new TextBlock
                {
                    Text = L10n.T("已经存在这台设备上。用在哪？"),
                    FontSize = 12,
                    Foreground = ThemeHelper.Brush(p.T2),
                });

                var cardsCol = new StackPanel { Spacing = 8 };
                if (!string.IsNullOrEmpty(forBookId) && !string.IsNullOrEmpty(forRoleName))
                {
                    cardsCol.Children.Add(MakeChoiceCard("people", $"给「{forRoleName}」配音", "立即设为这个角色的专属声音", highlight: true, () =>
                    {
                        try
                        {
                            var prev = library.Core.BookRoles(forBookId).FirstOrDefault(r => r.Name == forRoleName)?.Voice;
                            library.Core.SetBookRole(forBookId, forRoleName, new RoleVoice(savedCard.Id, prev?.Rate ?? 1.0, prev?.Pitch ?? 0.0, prev?.Right ?? false));
                            DramaSynthService.InvalidateCast(forBookId);
                            App.CurrentApp.BroadcastVoiceChanged();
                        }
                        catch { }
                        dlgRef?.Hide();
                    }));
                }

                cardsCol.Children.Add(MakeChoiceCard("headphones", "全书（旁白）", "设为当前语种默认朗读旁白声音", highlight: string.IsNullOrEmpty(forRoleName), () =>
                {
                    string targetLang = !string.IsNullOrEmpty(forBookId) &&
                                        bookLang.StartsWith(lang == "en" ? "en" : "zh", StringComparison.OrdinalIgnoreCase)
                        ? bookLang
                        : (lang == "en" ? "en" : "zh-Hans");
                    library.Prefs.SetVoiceForLanguage(targetLang, savedCard.Id);
                    if (lang == "en") library.Prefs.VoiceIdEn = savedCard.Id;
                    else library.Prefs.VoiceId = savedCard.Id;
                    library.SavePrefs();
                    App.CurrentApp.BroadcastVoiceChanged();
                    dlgRef?.Hide();
                }));

                if (string.IsNullOrEmpty(forRoleName))
                {
                    cardsCol.Children.Add(MakeChoiceCard("sync-arrows", "跨设备跟着同步", "把这张声音卡随书库一起同步到其他设备", highlight: false, () =>
                    {
                        try
                        {
                            string dir = System.IO.Path.Combine(library.DataDir, "clones", savedCard.Id);
                            Directory.CreateDirectory(dir);
                            savedCard = library.Core.CardSave(null, savedCard with { Sync = true, Version = savedCard.Version + 1 }, dir);
                        }
                        catch { }
                        dlgRef?.Hide();
                    }));
                }

                cardsCol.Children.Add(MakeChoiceCard("bookmark", "先放着", "保存在「我的声音」里，随时在换声音里选用", highlight: false, () =>
                {
                    dlgRef?.Hide();
                }));

                outerRoot.Children.Add(cardsCol);
            }

            if (!string.IsNullOrWhiteSpace(inlineError))
            {
                outerRoot.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(10),
                    Background = ThemeHelper.Brush(Color.FromArgb(20, p.Danger.R, p.Danger.G, p.Danger.B)),
                    BorderBrush = ThemeHelper.Brush(Color.FromArgb(64, p.Danger.R, p.Danger.G, p.Danger.B)),
                    BorderThickness = new Thickness(0.75),
                    Padding = new Thickness(12, 9, 12, 9),
                    Child = L10n.Skip(new TextBlock
                    {
                        Text = inlineError,
                        FontSize = 11.5,
                        Foreground = ThemeHelper.Brush(p.Danger),
                        TextWrapping = TextWrapping.Wrap,
                    }),
                });
            }

            L10n.ApplyTree(outerRoot);
            YYChrome.AnimateEntrance(outerRoot, fromY: 5f, durationMs: 140);
        }

        RebuildSheet();
        dlgRef = YYChrome.CreateMacSheet(xamlRoot, dark, 348, outerRoot, maxHeight: 680, tint: useTint);
        await dlgRef.ShowSafeAsync(useTint);

        recTimer.Stop();
        speakingAnimTimer.Stop();
        StopClonePreview();
        AudioPlayer.StopActivePreview();
        if (isRecording && capture is not null)
        {
            try { await capture.StopRecordAsync(); } catch { }
        }
        try { recStream?.Dispose(); } catch { }
        try { capture?.Dispose(); } catch { }

        return savedCard;
    }

    internal static List<string> FormatFaultLines(CardCheck check)
    {
        var lines = new List<string>();
        foreach (var fault in check.Faults)
        {
            switch (fault)
            {
                case CardFault.TooShort:
                    lines.Add($"只录了 {check.Seconds:0.1} 秒 · 念完上面那句话（6–15 秒）会更稳");
                    break;
                case CardFault.TooLong:
                    lines.Add($"录了 {check.Seconds:0.1} 秒 · 15 秒以内就够");
                    break;
                case CardFault.TooQuiet:
                    lines.Add($"说话音量 {(int)Math.Round(check.SpeechDb)} dBFS · 离麦克风近一点再念");
                    break;
                case CardFault.TooLoud:
                    lines.Add($"说话音量 {(int)Math.Round(check.SpeechDb)} dBFS · 稍微拿远一点");
                    break;
                case CardFault.Clipped:
                    lines.Add($"有 {check.ClippedRatio * 100:0.1}% 的采样削波 · 稍微拿远一点");
                    break;
                case CardFault.Noisy:
                    lines.Add($"信噪比 {(int)Math.Round(check.SnrDb)} dB（需 ≥ 20 dB）");
                    break;
                case CardFault.WrongWords:
                    lines.Add($"听写核对错字率 {(int)Math.Round((check.Cer ?? 0f) * 100)}% · 照着卡片上的字念即可");
                    break;
            }
        }
        return lines;
    }

    private static async Task<(double Level, double DbFs)> SampleLatestRmsLevelAsync(InMemoryRandomAccessStream stream)
    {
        try
        {
            ulong size = stream.Size;
            if (size <= 48) return (0, -60.0);
            ulong windowBytes = 3840; // 1920 samples = 80ms @ 24kHz PCM16
            ulong start = size > 44 + windowBytes ? size - windowBytes : 44;
            if ((start & 1UL) != 0) start++;
            uint len = (uint)Math.Min(windowBytes, size - start);
            if (len < 4) return (0, -60.0);

            using var input = stream.GetInputStreamAt(start);
            using var reader = new DataReader(input);
            uint loaded = await reader.LoadAsync(len);
            if (loaded < 4) return (0, -60.0);
            byte[] buf = new byte[loaded];
            reader.ReadBytes(buf);

            int samples = buf.Length / 2;
            double sumSq = 0;
            for (int i = 0; i < samples; i++)
            {
                short s = BitConverter.ToInt16(buf, i * 2);
                double norm = s / 32768.0;
                sumSq += norm * norm;
            }
            double rms = Math.Sqrt(sumSq / Math.Max(1, samples));
            double dbFs = rms > 1e-5 ? 20.0 * Math.Log10(rms) : -60.0;
            return (Math.Clamp((dbFs + 48.0) / 36.0, 0.0, 1.0), dbFs);
        }
        catch
        {
            return (0, -60.0);
        }
    }

    /// <summary>
    /// 1:1 <c>ClonedCardDetailView</c> + <c>StyleClipRecordSheet</c> (<c>CloneVoiceView.swift:762-1206</c>):
    /// rename, re-record base voice, sync toggle, ElevenLabs opt-in toggle, 6 emotional style clips, and confirmed delete.
    /// </summary>
    public static async Task ShowClonedCardDetailDialogAsync(
        Window ownerWindow, XamlRoot xamlRoot, LibraryService library, string cardId, Action? onChanged = null)
    {
        bool dark = ThemeHelper.IsDark(xamlRoot.Content is FrameworkElement fe ? fe.ActualTheme : ElementTheme.Light);
        var p = ThemeHelper.Palette(dark);
        var useTint = library.Prefs.Tint;
        var tint = ThemeHelper.TintColors(useTint, dark);

        var outer = new StackPanel { Spacing = 12, Padding = new Thickness(18, 16, 18, 16) };
        ContentDialog? dlgRef = null;
        bool deleted = false;
        CardStyle? pendingRecordStyle = null;
        bool pendingRename = false;
        bool pendingRerecordBase = false;
        bool pendingDeleteConfirm = false;

        void Rebuild()
        {
            outer.Children.Clear();
            var card = library.LiveVoice.GetClonedCards().FirstOrDefault(c => c.Id == cardId);
            if (card is null)
            {
                outer.Children.Add(YYChrome.MakeSheetHeader("克隆声音详情", null, () => dlgRef?.Hide(), dark));
                outer.Children.Add(new TextBlock { Text = L10n.T("找不到该克隆声音卡。"), Foreground = ThemeHelper.Brush(p.T2) });
                return;
            }

            outer.Children.Add(YYChrome.MakeSheetHeader(card.Name, "克隆声音", () => dlgRef?.Hide(), dark));

            // 1. 6 Emotional Style Clips
            var styleHdr = new Grid();
            styleHdr.Children.Add(new TextBlock { Text = L10n.T("加上语气"), FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
            styleHdr.Children.Add(new TextBlock { Text = L10n.T("可只录几档，没录的用平常语气"), FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), HorizontalAlignment = HorizontalAlignment.Right });
            outer.Children.Add(styleHdr);

            var stylesStack = new StackPanel { Spacing = 0 };
            for (int idx = 0; idx < EmotionStyles.Length; idx++)
            {
                if (idx > 0)
                    stylesStack.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line) });

                var (st, lbl, _) = EmotionStyles[idx];
                var existing = card.Clips.FirstOrDefault(c => c.Style == st);
                var rg = new Grid { ColumnSpacing = 8, Padding = new Thickness(4, 7, 4, 7) };
                rg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                rg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                rg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var meta = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
                meta.Children.Add(new TextBlock { Text = L10n.T(lbl), FontSize = 12.5, FontWeight = FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1) });
                meta.Children.Add(new TextBlock
                {
                    Text = existing is not null ? $"✓ {existing.Seconds:0.1}s" : L10n.T("未录"),
                    FontSize = 11,
                    Foreground = ThemeHelper.Brush(existing is not null ? tint.Accent : p.T3),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                rg.Children.Add(meta);

                if (existing is not null)
                {
                    var playB = YYChrome.MakeSheetPillButton("▶ 试听", async () =>
                    {
                        try
                        {
                            byte[]? synthWav = null;
                            if (library.LiveVoice.IsReady && CardVoiceEngine.SpeaksLanguage(card.Lang))
                            {
                                string sampleLine;
                                try { sampleLine = library.Core.CardScript(card.Lang, st); }
                                catch { sampleLine = existing.Text; }
                                synthWav = await CardVoiceEngine.Instance.SynthesizeWavAsync(
                                    library.Core, library.LiveVoice, library.DataDir,
                                    card, null, sampleLine, card.Lang, st);
                            }

                            if (synthWav is { Length: > 44 })
                            {
                                await PlayWavBytesOnceAsync(synthWav);
                            }
                            else
                            {
                                string? clipPath = library.Core.CardClipPath(null, card.Id, st);
                                if (!string.IsNullOrEmpty(clipPath) && File.Exists(clipPath))
                                    await PlayWavBytesOnceAsync(await File.ReadAllBytesAsync(clipPath));
                            }
                        }
                        catch { }
                    }, dark, useTint, kind: "secondary", height: 25, horizontalPadding: 10);
                    Grid.SetColumn(playB, 1);
                    rg.Children.Add(playB);
                }

                CardStyle capSt = st;
                var recB = YYChrome.MakeSheetPillButton(existing is not null ? "重录" : "录制", () =>
                {
                    pendingRecordStyle = capSt;
                    dlgRef?.Hide();
                }, dark, useTint, kind: existing is not null ? "secondary" : "warm", height: 25, horizontalPadding: 11);
                Grid.SetColumn(recB, 2);
                rg.Children.Add(recB);
                stylesStack.Children.Add(rg);
            }

            outer.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(12),
                Background = ThemeHelper.Brush(p.Card),
                BorderBrush = ThemeHelper.Brush(p.Line),
                BorderThickness = new Thickness(0.75),
                Padding = new Thickness(10, 2, 10, 2),
                Child = stylesStack,
            });

            // 2. Sync & ElevenLabs toggles
            var optsStack = new StackPanel { Spacing = 0 };
            var syncRow = new Grid { Padding = new Thickness(4, 6, 4, 6) };
            syncRow.Children.Add(new TextBlock { Text = L10n.T("跨设备跟着同步"), FontSize = 12.5, Foreground = ThemeHelper.Brush(p.T1), VerticalAlignment = VerticalAlignment.Center });
            var syncSw = new ToggleSwitch { IsOn = card.Sync, HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 0, OnContent = "", OffContent = "" };
            syncSw.Toggled += (_, _) =>
            {
                try
                {
                    string dir = System.IO.Path.Combine(library.DataDir, "clones", card.Id);
                    Directory.CreateDirectory(dir);
                    library.Core.CardSave(null, card with { Sync = syncSw.IsOn, Version = card.Version + 1 }, dir);
                    onChanged?.Invoke();
                }
                catch { }
            };
            syncRow.Children.Add(syncSw);
            optsStack.Children.Add(syncRow);

            if (library.CloudVoices.IsConfigured(CloudTtsProvider.ElevenLabs))
            {
                optsStack.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line) });
                var elRow = new Grid { Padding = new Thickness(4, 6, 4, 6) };
                elRow.Children.Add(new TextBlock { Text = L10n.T("优先使用 ElevenLabs 云端朗读"), FontSize = 12.5, Foreground = ThemeHelper.Brush(p.T1), VerticalAlignment = VerticalAlignment.Center });
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
                optsStack.Children.Add(elRow);
            }

            outer.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(12),
                Background = ThemeHelper.Brush(p.Card),
                BorderBrush = ThemeHelper.Brush(p.Line),
                BorderThickness = new Thickness(0.75),
                Padding = new Thickness(10, 2, 10, 2),
                Child = optsStack,
            });

            // 3. Footer: Rename + Re-record + Confirmed Delete (1:1 CloneVoiceView.swift:830-850)
            outer.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line) });
            var footRow = new Grid();
            var leftFoot = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var renameBtn = YYChrome.MakeSheetPillButton("重命名", () =>
            {
                pendingRename = true;
                dlgRef?.Hide();
            }, dark, useTint, kind: "secondary", height: 30, horizontalPadding: 14);
            leftFoot.Children.Add(renameBtn);

            var rerecordBtn = YYChrome.MakeSheetPillButton("重新录一段", () =>
            {
                pendingRerecordBase = true;
                dlgRef?.Hide();
            }, dark, useTint, kind: "secondary", height: 30, horizontalPadding: 14);
            leftFoot.Children.Add(rerecordBtn);
            footRow.Children.Add(leftFoot);

            Color dangerBg = dark ? Color.FromArgb(36, 229, 128, 111) : Color.FromArgb(22, 194, 71, 58);
            var delBtn = new Button
            {
                Height = 30,
                MinHeight = 30,
                Padding = new Thickness(14, 0, 14, 0),
                CornerRadius = new CornerRadius(15),
                Background = ThemeHelper.Brush(dangerBg),
                BorderBrush = ThemeHelper.Brush(Color.FromArgb(56, p.Danger.R, p.Danger.G, p.Danger.B)),
                BorderThickness = new Thickness(0.75),
                HorizontalAlignment = HorizontalAlignment.Right,
                UseSystemFocusVisuals = false,
                Content = new TextBlock { Text = L10n.T("删除"), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.Danger) },
            };
            delBtn.Click += (_, _) =>
            {
                pendingDeleteConfirm = true;
                dlgRef?.Hide();
            };
            footRow.Children.Add(delBtn);
            outer.Children.Add(footRow);

            L10n.ApplyTree(outer);
        }

        while (!deleted)
        {
            pendingRecordStyle = null;
            pendingRename = false;
            pendingRerecordBase = false;
            pendingDeleteConfirm = false;
            Rebuild();

            var scroll = new ScrollViewer { MaxHeight = 560, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = outer };
            dlgRef = YYChrome.CreateMacSheet(xamlRoot, dark, 380, scroll, maxHeight: 620, tint: useTint);
            await dlgRef.ShowSafeAsync(useTint);
            StopClonePreview();
            AudioPlayer.StopActivePreview();

            var latestCard = library.LiveVoice.GetClonedCards().FirstOrDefault(c => c.Id == cardId);
            if (latestCard is null) break;

            if (pendingRerecordBase)
            {
                var updated = await ShowCloneVoiceDialogAsync(ownerWindow, xamlRoot, library, existingCardId: latestCard.Id);
                if (updated is not null) onChanged?.Invoke();
                continue;
            }

            if (pendingRename)
            {
                string? nextName = await YYChrome.ShowPromptAsync(
                    xamlRoot, dark, "重命名", placeholder: "名字", initialText: latestCard.Name, confirmText: "保存", tint: useTint);
                if (!string.IsNullOrWhiteSpace(nextName) && nextName.Trim() != latestCard.Name)
                {
                    try
                    {
                        string dir = System.IO.Path.Combine(library.DataDir, "clones", latestCard.Id);
                        Directory.CreateDirectory(dir);
                        library.Core.CardSave(null, latestCard with { Name = nextName.Trim(), Version = latestCard.Version + 1 }, dir);
                        App.CurrentApp.BroadcastVoiceChanged();
                        onChanged?.Invoke();
                    }
                    catch { }
                }
                continue;
            }

            if (pendingDeleteConfirm)
            {
                bool confirmed = await YYChrome.ShowConfirmAsync(
                    xamlRoot,
                    dark,
                    $"删除“{latestCard.Name}”？",
                    "这台设备上的参考录音会一并删掉。",
                    confirmText: "删除",
                    cancelText: "取消",
                    destructive: true,
                    tint: useTint);
                if (confirmed)
                {
                    DeleteClonedCard(library, latestCard.Id);
                    deleted = true;
                    onChanged?.Invoke();
                    break;
                }
                continue;
            }

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
        var useTint = library.Prefs.Tint;
        var tint = ThemeHelper.TintColors(useTint, dark);
        var kami = dark ? YYPapers.Kaminight : YYPapers.Kami;
        var serifFont = new FontFamily(YYFontFamily.Serif.WindowsFamily());
        bool zh = !card.Lang.StartsWith("en", StringComparison.OrdinalIgnoreCase);

        string styleName = EmotionStyles.FirstOrDefault(x => x.Style == style).Label ?? style.ToString();
        string slug = EmotionStyles.FirstOrDefault(x => x.Style == style).FileSlug ?? style.ToString().ToLowerInvariant();
        string instruction = "";
        string script = "";
        try { instruction = library.Core.CardStyleInstruction(style, zh); } catch { }
        try { script = library.Core.CardScript(card.Lang, style); } catch { script = "请用对应情绪清晰朗读 6 到 12 秒。"; }

        float[]? samples24k = null;
        CardCheck? chk = null;
        string transcript = script;
        bool imported = false;
        bool committed = false;

        MediaCapture? capture = null;
        InMemoryRandomAccessStream? stream = null;
        bool recording = false;
        bool stopping = false;
        bool samplingStyleMeter = false;
        string? styleMicError = null;
        DateTime recStartUtc = DateTime.UtcNow;
        double speechSec = 0, silenceSec = 0;

        const int BarCount = 28;
        var barLevels = new double[BarCount];
        var barBorders = new Border[BarCount];
        TextBlock? recHint = null;
        ContentDialog? dlgRef = null;

        var recTimer = new DispatcherTimer { Interval = System.TimeSpan.FromMilliseconds(80) };
        var outer = new StackPanel { Spacing = 12, Padding = new Thickness(18, 16, 18, 16) };

        async Task StopStyleRecAsync()
        {
            if (!recording || stopping) return;
            stopping = true;
            recording = false;
            recTimer.Stop();

            byte[]? bytes = null;
            try
            {
                if (capture is not null) await capture.StopRecordAsync();
                if (stream is not null && stream.Size > 44)
                {
                    stream.Seek(0);
                    bytes = new byte[stream.Size];
                    await stream.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
                }
            }
            catch { }
            finally
            {
                try { stream?.Dispose(); } catch { }
                stream = null;
                try { capture?.Dispose(); } catch { }
                capture = null;
                stopping = false;
            }

            if (bytes is { Length: > 1024 })
            {
                string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"yuyue-style-{Guid.NewGuid():N}.wav");
                try
                {
                    await File.WriteAllBytesAsync(tmp, bytes);
                    imported = false;
                    transcript = script;
                    samples24k = await Task.Run(() => library.Core.CardDecodeClip(tmp, 24_000));
                    string? heard = await WindowsTranscriber.TranscribeAsync(library, tmp, card.Lang);
                    chk = await Task.Run(() => library.Core.CardCheck(
                        samples24k, 24_000, transcript, string.IsNullOrWhiteSpace(heard) ? null : heard.Trim()));
                }
                catch
                {
                    chk = new CardCheck(false, 0, -60, 0, 0, 1, [CardFault.TooShort], ["处理录音失败，请重试"]);
                }
                finally { try { File.Delete(tmp); } catch { } }
            }
            Rebuild();
        }

        recTimer.Tick += async (_, _) =>
        {
            if (!recording || stream is null || samplingStyleMeter) return;
            samplingStyleMeter = true;
            double lvl;
            double dbFs;
            try
            {
                (lvl, dbFs) = await SampleLatestRmsLevelAsync(stream);
            }
            finally
            {
                samplingStyleMeter = false;
            }
            if (!recording || stream is null) return;
            double elapsed = (DateTime.UtcNow - recStartUtc).TotalSeconds;
            Array.Copy(barLevels, 1, barLevels, 0, BarCount - 1);
            barLevels[BarCount - 1] = lvl;
            for (int i = 0; i < BarCount; i++)
                if (barBorders[i] is not null) barBorders[i].Height = Math.Clamp(4.0 + barLevels[i] * 18.0, 4.0, 22.0);
            if (recHint is not null)
                recHint.Text = L10n.T($"正在录音 {(int)elapsed / 60}:{(int)elapsed % 60:D2} · 念完停顿 1.2 秒自动停");

            if (dbFs > -38.0) { speechSec += 0.08; silenceSec = 0; }
            else if (speechSec >= 0.4 && elapsed >= 2.0)
            {
                silenceSec += 0.08;
                if (silenceSec >= 1.2) { await StopStyleRecAsync(); return; }
            }
            if (elapsed >= 22.0) await StopStyleRecAsync();
        };

        void Rebuild()
        {
            outer.Children.Clear();
            recHint = null;
            outer.Children.Add(YYChrome.MakeSheetHeader($"录制「{styleName}」", instruction, () => dlgRef?.Hide(), dark));

            if (imported)
            {
                var tBox = new TextBox
                {
                    Header = L10n.T("录音里念的字"),
                    Text = transcript,
                    FontFamily = serifFont,
                    FontSize = 13.5,
                    AcceptsReturn = true,
                    TextWrapping = TextWrapping.Wrap,
                    CornerRadius = new CornerRadius(10),
                    Background = ThemeHelper.Brush(p.Card),
                };
                tBox.TextChanged += (_, _) =>
                {
                    transcript = tBox.Text;
                    if (samples24k is { Length: >= 12_000 } && !string.IsNullOrWhiteSpace(transcript))
                    {
                        try { chk = library.Core.CardCheck(samples24k, 24_000, transcript.Trim(), null); } catch { }
                    }
                };
                outer.Children.Add(tBox);
            }
            else
            {
                outer.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(12),
                    Background = ThemeHelper.Brush(kami.Bg),
                    BorderBrush = ThemeHelper.Brush(kami.Line),
                    BorderThickness = new Thickness(0.75),
                    Padding = new Thickness(14, 12, 14, 12),
                    Child = L10n.Skip(new TextBlock
                    {
                        Text = script,
                        FontFamily = serifFont,
                        FontSize = 14.5,
                        LineHeight = 24,
                        Foreground = ThemeHelper.Brush(kami.Ink),
                        TextWrapping = TextWrapping.Wrap,
                    }),
                });
            }

            var recCenter = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 2) };
            var micBtn = new Button
            {
                Width = 50,
                Height = 50,
                MinWidth = 50,
                MinHeight = 50,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(25),
                HorizontalAlignment = HorizontalAlignment.Center,
                Background = ThemeHelper.Brush(recording ? Color.FromArgb(24, p.Danger.R, p.Danger.G, p.Danger.B) : tint.Soft),
                BorderBrush = ThemeHelper.Brush(recording ? Color.FromArgb(96, p.Danger.R, p.Danger.G, p.Danger.B) : tint.Edge),
                BorderThickness = new Thickness(1),
                UseSystemFocusVisuals = false,
                Content = recording
                    ? (UIElement)new Border { Width = 13, Height = 13, CornerRadius = new CornerRadius(3), Background = ThemeHelper.Brush(p.Danger) }
                    : YYIcons.Make("mic", 18, ThemeHelper.Brush(tint.Accent)),
            };
            micBtn.Click += async (_, _) =>
            {
                if (recording)
                {
                    await StopStyleRecAsync();
                }
                else
                {
                    try
                    {
                        styleMicError = null;
                        AudioPlayer.StopActivePreview();
                        StopClonePreview();
                        App.CurrentApp?.ClaimAudioPlayback(ClonePreviewClaimant, () => _ = StopStyleRecAsync());
                        capture = new MediaCapture();
                        await capture.InitializeAsync(new MediaCaptureInitializationSettings { StreamingCaptureMode = StreamingCaptureMode.Audio });
                        stream = new InMemoryRandomAccessStream();
                        var prof = MediaEncodingProfile.CreateWav(AudioEncodingQuality.High);
                        prof.Audio.SampleRate = 24_000; prof.Audio.ChannelCount = 1; prof.Audio.BitsPerSample = 16;
                        await capture.StartRecordToStreamAsync(prof, stream);
                        Array.Clear(barLevels);
                        speechSec = 0; silenceSec = 0;
                        recording = true;
                        recStartUtc = DateTime.UtcNow;
                        Rebuild();
                        recTimer.Start();
                    }
                    catch (Exception ex)
                    {
                        try { stream?.Dispose(); } catch { }
                        stream = null;
                        try { capture?.Dispose(); } catch { }
                        capture = null;
                        recording = false;
                        styleMicError = $"{L10n.T("无法打开麦克风")}：{ex.Message}";
                        Rebuild();
                    }
                }
            };
            recCenter.Children.Add(micBtn);

            var waveRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, Height = 20, HorizontalAlignment = HorizontalAlignment.Center };
            for (int i = 0; i < BarCount; i++)
            {
                var b = new Border
                {
                    Width = 3,
                    Height = recording ? Math.Clamp(4.0 + barLevels[i] * 18.0, 4.0, 22.0) : 4.5,
                    CornerRadius = new CornerRadius(1.5),
                    VerticalAlignment = VerticalAlignment.Center,
                    Background = ThemeHelper.Brush(recording ? tint.Accent : p.Line),
                };
                barBorders[i] = b;
                waveRow.Children.Add(b);
            }
            recCenter.Children.Add(waveRow);

            recHint = new TextBlock
            {
                Text = L10n.T(recording ? "正在录音 0:00 · 念完停顿 1.2 秒自动停" : "点一下开始录，念完停顿 1.2 秒自动停"),
                FontSize = 11,
                Foreground = ThemeHelper.Brush(p.T2),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            recCenter.Children.Add(recHint);
            outer.Children.Add(recCenter);

            if (!string.IsNullOrWhiteSpace(styleMicError))
            {
                outer.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(10),
                    Background = ThemeHelper.Brush(Color.FromArgb(20, p.Danger.R, p.Danger.G, p.Danger.B)),
                    Padding = new Thickness(12, 8, 12, 8),
                    Child = L10n.Skip(new TextBlock
                    {
                        Text = styleMicError,
                        FontSize = 11.5,
                        Foreground = ThemeHelper.Brush(p.Danger),
                        TextWrapping = TextWrapping.Wrap,
                    }),
                });
            }
            else if (chk is not null)
            {
                outer.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(10),
                    Background = ThemeHelper.Brush(Color.FromArgb(20, (chk.Ok ? p.Ok : p.Danger).R, (chk.Ok ? p.Ok : p.Danger).G, (chk.Ok ? p.Ok : p.Danger).B)),
                    Padding = new Thickness(12, 8, 12, 8),
                    Child = new TextBlock
                    {
                        Text = chk.Ok
                            ? $"✓ {L10n.T("情绪样本合格")}（{chk.Seconds:0.1}s · {chk.SnrDb:0} dB）"
                            : "✕ " + (chk.Messages.FirstOrDefault() ?? string.Join("；", FormatFaultLines(chk))),
                        FontSize = 11.5,
                        Foreground = ThemeHelper.Brush(chk.Ok ? p.Ok : p.Danger),
                        TextWrapping = TextWrapping.Wrap,
                    },
                });
            }

            var foot = new Grid();
            var impLink = new Button
            {
                Content = L10n.T("或者选一段已有的录音"),
                Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
                Foreground = ThemeHelper.Brush(tint.Accent),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
            };
            impLink.Click += async (_, _) =>
            {
                if (recording) return;
                string? filePath = await NativePickerHelper.PickSingleFileAsync(ownerWindow, [".wav", ".mp3", ".m4a", ".flac"]);
                if (string.IsNullOrEmpty(filePath)) return;
                try
                {
                    samples24k = await Task.Run(() => library.Core.CardDecodeClip(filePath, 24_000));
                    string? heard = await WindowsTranscriber.TranscribeAsync(library, filePath, card.Lang);
                    imported = true;
                    if (!string.IsNullOrWhiteSpace(heard)) transcript = heard.Trim();
                    chk = await Task.Run(() => library.Core.CardCheck(samples24k, 24_000, transcript.Trim(), null));
                }
                catch
                {
                    chk = new CardCheck(false, 0, -60, 0, 0, 1, [CardFault.TooShort], ["解码音频失败"]);
                }
                Rebuild();
            };
            foot.Children.Add(impLink);

            var saveBtn = YYChrome.MakeSheetPillButton("保存情绪样本", () =>
            {
                if (chk is { Ok: true })
                {
                    committed = true;
                    dlgRef?.Hide();
                }
            }, dark, useTint, kind: "warm", height: 30, horizontalPadding: 14);
            saveBtn.HorizontalAlignment = HorizontalAlignment.Right;
            saveBtn.IsEnabled = chk is { Ok: true };
            saveBtn.Opacity = chk is { Ok: true } ? 1.0 : 0.4;
            foot.Children.Add(saveBtn);
            outer.Children.Add(foot);

            L10n.ApplyTree(outer);
        }

        Rebuild();
        dlgRef = YYChrome.CreateMacSheet(xamlRoot, dark, 348, outer, maxHeight: 620, tint: useTint);
        await dlgRef.ShowSafeAsync(useTint);

        recTimer.Stop();
        if (recording && capture is not null) { try { await capture.StopRecordAsync(); } catch { } }
        try { stream?.Dispose(); } catch { }
        try { capture?.Dispose(); } catch { }

        if (!committed || samples24k is null || chk is not { Ok: true }) return;

        try
        {
            string stageDir = System.IO.Path.Combine(library.DataDir, "clones", card.Id);
            Directory.CreateDirectory(stageDir);
            string fileName = $"{slug}.wav";
            float secs = library.Core.CardWriteWav(System.IO.Path.Combine(stageDir, fileName), samples24k, 24_000);
            var updatedClips = card.Clips.Where(c => c.Style != style)
                .Append(new CardClip(style, fileName, transcript.Trim(), secs))
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
            string dir = System.IO.Path.Combine(library.DataDir, "clones", cardId);
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
        StopClonePreview();
        AudioPlayer.StopActivePreview();

        var cts = new CancellationTokenSource();
        var mem = new InMemoryRandomAccessStream();
        MediaSource? src = null;
        MediaPlayer? mp = null;

        try
        {
            await mem.WriteAsync(wav.AsBuffer());
            mem.Seek(0);
            src = MediaSource.CreateFromStream(mem, "audio/wav");
            mp = new MediaPlayer { AutoPlay = false };

            lock (ClonePreviewGate)
            {
                ActiveClonePreviewCts = cts;
                ActiveClonePreviewPlayer = mp;
            }
            try { App.CurrentApp?.ClaimAudioPlayback(ClonePreviewClaimant, StopClonePreview); } catch { }

            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            mp.MediaEnded += (_, _) => done.TrySetResult();
            mp.MediaFailed += (_, _) => done.TrySetResult();
            using var reg = cts.Token.Register(() => done.TrySetResult());

            mp.Source = src;
            mp.Play();
            await Task.WhenAny(done.Task, Task.Delay(15_000, cts.Token));
        }
        catch (OperationCanceledException) { }
        finally
        {
            lock (ClonePreviewGate)
            {
                if (ReferenceEquals(ActiveClonePreviewPlayer, mp))
                {
                    ActiveClonePreviewPlayer = null;
                    ActiveClonePreviewCts = null;
                }
            }
            try { mp?.Pause(); if (mp is not null) mp.Source = null; } catch { }
            try { src?.Dispose(); } catch { }
            try { mp?.Dispose(); } catch { }
            try { mem.Dispose(); } catch { }
            try { cts.Dispose(); } catch { }
        }
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
        uint targetSection = focusSection;
        DramaSynthService.UpdateDramaCaps(library, bookId, lang, null, ttsActive: DramaSynthService.IsPreRendering(bookId), allowLlm: true);
        SoundLibraryService.Register(library.Core, library.DataDir);

        _ = Task.Run(async () =>
        {
            var nextTaskGate = new SemaphoreSlim(1, 1);
            var returnedOutOfScope = new HashSet<string>(StringComparer.Ordinal);
            int inFlight = 0;
            const int Parallelism = 2;

            static bool IsFatalModelError(string? msg) =>
                !string.IsNullOrEmpty(msg) && (
                    msg.Contains("401", StringComparison.Ordinal) ||
                    msg.Contains("402", StringComparison.Ordinal) ||
                    msg.Contains("403", StringComparison.Ordinal) ||
                    msg.Contains("密钥", StringComparison.Ordinal) ||
                    msg.Contains("余额", StringComparison.Ordinal) ||
                    msg.Contains("额度", StringComparison.Ordinal) ||
                    msg.Contains("上限", StringComparison.Ordinal));

            async Task RunSlotAsync()
            {
                while (!ct.IsCancellationRequested)
                {
                    DramaTask? task;
                    uint curFocus = state.SectionOnly ? targetSection : state.FocusSection;
                    await nextTaskGate.WaitAsync(ct);
                    try
                    {
                        task = library.Core.DramaNextTask(bookId, curFocus);
                    }
                    catch (Exception ex)
                    {
                        state.LastError = ex.Message;
                        break;
                    }
                    finally { nextTaskGate.Release(); }

                    if (task is null)
                    {
                        if (Volatile.Read(ref inFlight) > 0)
                        {
                            await Task.Delay(1500, ct);
                            continue;
                        }
                        break;
                    }

                    if (task is DramaTask.Llm llm)
                    {
                        if (state.SectionOnly && !TaskMatchesSection(llm.Id, bookId, curFocus))
                        {
                            try { library.Core.DramaSubmit(bookId, llm.Id, new DramaResult.Returned()); } catch { }
                            bool alreadySeen;
                            lock (returnedOutOfScope)
                            {
                                alreadySeen = !returnedOutOfScope.Add(llm.Id);
                            }
                            if (Volatile.Read(ref inFlight) > 0)
                            {
                                await Task.Delay(1500, ct);
                                continue;
                            }
                            if (alreadySeen || returnedOutOfScope.Count > 0)
                                break;
                            break;
                        }

                        Interlocked.Increment(ref inFlight);
                        try
                        {
                            // Put <schema> in system prompt to maximize prompt cache hit rate (1:1 DramaWork.swift)
                            string systemPrompt = string.IsNullOrWhiteSpace(llm.Schema)
                                ? llm.System
                                : $"{llm.System}\n\n<schema>\n{llm.Schema}\n</schema>";
                            int maxTokens = llm.MaxOutputTokens > 0
                                ? Math.Clamp((int)llm.MaxOutputTokens, 256, 32768)
                                : 2048;
                            bool useFast = llm.Tier != LlmTier.Strong;

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
                                        fast: useFast,
                                        cachePrefixLen: (int)llm.CachePrefixLen,
                                        source: UsageSource.Drama,
                                        ct: ct);
                                    lastErr = null;
                                    break;
                                }
                                catch (OperationCanceledException) { throw; }
                                catch (Exception ex) when (attempt < backoffs.Length && !IsFatalModelError(ex.Message))
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
                                Interlocked.Add(ref state.SentChars, answer.Length);
                                try { library.Core.DramaSubmit(bookId, llm.Id, new DramaResult.Llm(answer)); } catch { }
                                DramaSynthService.InvalidateCast(bookId);
                                lock (returnedOutOfScope) returnedOutOfScope.Clear();
                                onProgressUpdated();
                            }
                            else
                            {
                                string reason = lastErr ?? "模型未返回有效结果";
                                state.LastError = reason;
                                LastWorkerErrors[bookId] = reason;
                                bool isCast = llm.Id.StartsWith("cast:", StringComparison.Ordinal);
                                if (ct.IsCancellationRequested || IsFatalModelError(lastErr) || isCast)
                                {
                                    try { library.Core.DramaSubmit(bookId, llm.Id, new DramaResult.Returned()); } catch { }
                                    try { state.Cts.Cancel(); } catch { }
                                    break;
                                }

                                try { library.Core.DramaSubmit(bookId, llm.Id, new DramaResult.Failed(reason)); } catch { }
                                onProgressUpdated();
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            try { library.Core.DramaSubmit(bookId, llm.Id, new DramaResult.Returned()); } catch { }
                            throw;
                        }
                        finally
                        {
                            Interlocked.Decrement(ref inFlight);
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
                        if (Volatile.Read(ref inFlight) > 0)
                        {
                            await Task.Delay(1500, ct);
                            continue;
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
                DramaSynthService.UpdateDramaCaps(library, bookId, lang, null, ttsActive: DramaSynthService.IsPreRendering(bookId), allowLlm: false);
                state.Cts.Dispose();
                onProgressUpdated();
            }
        }, ct);
    }

    internal static bool TaskMatchesSection(string taskId, string bookId, uint targetSection)
    {
        if (string.IsNullOrEmpty(taskId)) return false;
        string[] parts = taskId.Split(':');
        if (parts.Length < 3) return false;
        string kind = parts[0];
        if (kind is not ("unquoted" or "attr" or "verify" or "direct"))
            return false;

        string prefix = $"{kind}:{bookId}:";
        if (taskId.StartsWith(prefix, StringComparison.Ordinal))
        {
            string rest = taskId[prefix.Length..];
            int colon = rest.IndexOf(':');
            string secPart = colon >= 0 ? rest[..colon] : rest;
            return uint.TryParse(secPart, out uint s) && s == targetSection;
        }
        return uint.TryParse(parts[2], out uint sec) && sec == targetSection;
    }

    private static Color ParseHexColor(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        string s = hex.Trim().TrimStart('#');
        if (s.Length == 6 &&
            byte.TryParse(s[0..2], System.Globalization.NumberStyles.HexNumber, null, out byte r) &&
            byte.TryParse(s[2..4], System.Globalization.NumberStyles.HexNumber, null, out byte g) &&
            byte.TryParse(s[4..6], System.Globalization.NumberStyles.HexNumber, null, out byte b))
        {
            return Color.FromArgb(255, r, g, b);
        }
        return fallback;
    }

    private static Border MakeDramaAvatar(string? text, Color color, double size = 26)
    {
        string glyph = string.IsNullOrWhiteSpace(text) ? "?" : text.Trim();
        return L10n.Skip(new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2.0),
            Background = ThemeHelper.Brush(Color.FromArgb(44, color.R, color.G, color.B)),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = glyph,
                FontSize = Math.Round(size * 0.44, 1),
                FontWeight = FontWeights.SemiBold,
                Foreground = ThemeHelper.Brush(color),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        });
    }

    private static string SliceScalarRange(string sentence, uint start, uint end)
    {
        var runes = sentence.EnumerateRunes().ToArray();
        int a = Math.Clamp((int)start, 0, runes.Length);
        int b = Math.Clamp((int)Math.Max(end, start), a, runes.Length);
        if (b <= a) return "";
        var sb = new System.Text.StringBuilder(b - a);
        for (int i = a; i < b; i++) sb.Append(runes[i].ToString());
        return sb.ToString().Trim();
    }

    internal static string FindCharacterSampleLine(LibraryService library, string bookId, string roleName, string language)
    {
        try
        {
            uint startSec = library.GetProgress(bookId)?.Position.Section ?? 0;
            uint totalSecs = 1;
            try { totalSecs = Math.Max(1u, (uint)library.GetBookDetail(bookId).Sections.Length); } catch { }

            // Check current section first, then nearby earlier/later sections (1:1 CastView.swift:295-307)
            var candidates = new List<uint> { startSec };
            for (uint d = 1; d <= 6; d++)
            {
                if (startSec >= d) candidates.Add(startSec - d);
                if (startSec + d < totalSecs) candidates.Add(startSec + d);
            }

            foreach (uint sec in candidates)
            {
                try
                {
                    var script = library.Core.SectionScript(bookId, sec);
                    var sentences = library.Core.Sentences(bookId, sec);
                    foreach (var ln in script.Lines)
                    {
                        if (ln.First != ln.Last || ln.Who is null) continue;
                        bool matches = ln.Who == roleName
                                       || ln.Who.EndsWith(roleName, StringComparison.Ordinal)
                                       || roleName.EndsWith(ln.Who, StringComparison.Ordinal);
                        if (!matches || ln.First >= sentences.Length) continue;
                        string quote = SliceScalarRange(sentences[ln.First], ln.Start, ln.End);
                        if (quote.Length >= 2) return quote;
                    }
                }
                catch { }

                try
                {
                    var sentences = library.Core.Sentences(bookId, sec);
                    for (uint si = 0; si < (uint)sentences.Length && si < 80; si++)
                    {
                        var segs = library.Core.RoleSentenceSegments(bookId, sec, si);
                        var hit = segs.FirstOrDefault(sg => sg.Role == roleName && sg.Text.Trim().Length >= 2);
                        if (hit is not null)
                            return CardVoiceEngine.PlainSpokenText(hit.Text).Trim('“', '”', '「', '」', '"', ' ');
                    }
                }
                catch { }
            }
        }
        catch { }

        return language.StartsWith("en", StringComparison.OrdinalIgnoreCase)
            ? $"Hello, I am {roleName}."
            : $"我是{roleName}。";
    }

    // ---------- 3. Searchable PeoplePicker (1:1 DramaViews.swift PeoplePicker: 340px CreateMacSheet) ----------

    public static async Task<UserChoice?> ShowPeoplePickerDialogAsync(
        XamlRoot xamlRoot, LibraryService library, string bookId, uint currentSection, string title = "选择说话人", bool includeGroup = true)
    {
        bool dark = ThemeHelper.IsDark(xamlRoot.Content is FrameworkElement fe ? fe.ActualTheme : ElementTheme.Light);
        var p = ThemeHelper.Palette(dark);
        var useTint = library.Prefs.Tint;
        var tint = ThemeHelper.TintColors(useTint, dark);

        CastPick[] everyone = await Task.Run(() =>
        {
            try { return library.Core.DramaEveryone(bookId, currentSection); }
            catch { return Array.Empty<CastPick>(); }
        });

        UserChoice? picked = null;
        ContentDialog? dlgRef = null;

        var outer = new StackPanel { Spacing = 10, Padding = new Thickness(16, 15, 16, 15) };
        outer.Children.Add(YYChrome.MakeSheetHeader(title, null, () => dlgRef?.Hide(), dark));

        var searchRow = new Grid { ColumnSpacing = 6 };
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var searchIcon = YYIcons.Make("search", 13, ThemeHelper.Brush(p.T3));
        searchIcon.VerticalAlignment = VerticalAlignment.Center;
        searchRow.Children.Add(searchIcon);

        var searchBox = new TextBox
        {
            PlaceholderText = L10n.T("搜索人物或输入新名字后回车…"),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            FontSize = 13,
            Padding = new Thickness(2, 4, 4, 4),
            VerticalAlignment = VerticalAlignment.Center,
        };
        YYChrome.StripTextBoxChrome(searchBox);
        Grid.SetColumn(searchBox, 1);
        searchRow.Children.Add(searchBox);

        outer.Children.Add(new Border
        {
            Height = 34,
            CornerRadius = new CornerRadius(9),
            Background = ThemeHelper.Brush(p.Inset),
            Padding = new Thickness(10, 0, 8, 0),
            Child = searchRow,
        });

        var listHost = new StackPanel { Spacing = 2 };

        Button MakePickRow(UIElement leading, string labelText, Color fg, bool bold, Action onSelect)
        {
            var g = new Grid { ColumnSpacing = 10 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.Children.Add(leading);
            var tb = L10n.Skip(new TextBlock
            {
                Text = labelText,
                FontSize = 12.5,
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = ThemeHelper.Brush(fg),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            Grid.SetColumn(tb, 1);
            g.Children.Add(tb);

            var btn = new Button
            {
                Content = g,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 6, 8, 6),
                UseSystemFocusVisuals = false,
            };
            btn.PointerEntered += (_, _) => btn.Background = ThemeHelper.Brush(p.Inset);
            btn.PointerExited += (_, _) => btn.Background = new SolidColorBrush(Colors.Transparent);
            btn.Click += (_, _) => onSelect();
            return btn;
        }

        void Rebuild(string q)
        {
            listHost.Children.Clear();
            string filter = q.Trim();

            if (!string.IsNullOrEmpty(filter) && !everyone.Any(c => string.Equals(c.Name, filter, StringComparison.OrdinalIgnoreCase)))
            {
                var plusCircle = new Border
                {
                    Width = 26,
                    Height = 26,
                    CornerRadius = new CornerRadius(13),
                    Background = ThemeHelper.Brush(tint.Soft),
                    Child = YYIcons.Make("plus", 11, ThemeHelper.Brush(tint.Accent)),
                };
                listHost.Children.Add(MakePickRow(plusCircle, $"{L10n.T("新的人物")}“{filter}”", tint.Accent, true, () =>
                {
                    picked = new UserChoice.Person(filter);
                    dlgRef?.Hide();
                }));
            }

            var matches = everyone.Where(c =>
                string.IsNullOrEmpty(filter) ||
                c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).Take(48);

            foreach (var person in matches)
            {
                string nm = person.Name;
                Color avColor = ParseHexColor(person.Color, tint.Accent);
                listHost.Children.Add(MakePickRow(MakeDramaAvatar(person.Avatar, avColor, 26), nm, p.T1, true, () =>
                {
                    picked = new UserChoice.Person(nm);
                    dlgRef?.Hide();
                }));
            }

            if (includeGroup && (string.IsNullOrEmpty(filter) || "众人".Contains(filter)))
            {
                var grpCircle = new Border
                {
                    Width = 26,
                    Height = 26,
                    CornerRadius = new CornerRadius(13),
                    Background = ThemeHelper.Brush(p.Inset),
                    Child = YYIcons.Make("people", 12, ThemeHelper.Brush(p.T2)),
                };
                listHost.Children.Add(MakePickRow(grpCircle, L10n.T("众人（齐声）"), p.T2, false, () =>
                {
                    picked = new UserChoice.Group("众人");
                    dlgRef?.Hide();
                }));
            }
        }

        searchBox.TextChanged += (_, _) => Rebuild(searchBox.Text);
        searchBox.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter && !string.IsNullOrWhiteSpace(searchBox.Text))
            {
                string q = searchBox.Text.Trim();
                var hit = everyone.FirstOrDefault(c => c.Name.Contains(q, StringComparison.OrdinalIgnoreCase));
                picked = new UserChoice.Person(hit?.Name ?? q);
                dlgRef?.Hide();
                e.Handled = true;
            }
        };
        Rebuild("");

        outer.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0.75),
            Padding = new Thickness(6, 4, 6, 4),
            Child = new ScrollViewer { MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = listHost },
        });

        L10n.ApplyTree(outer);
        dlgRef = YYChrome.CreateMacSheet(xamlRoot, dark, 340, outer, maxHeight: 480, tint: useTint);
        dlgRef.Opened += (_, _) => searchBox.Focus(FocusState.Programmatic);
        await dlgRef.ShowSafeAsync(useTint);
        return picked;
    }

    // ---------- 4. Active-Learning Unsure Speaker Review (1:1 DramaViews.swift UnsureView: 400px CreateMacSheet) ----------

    public static async Task ShowUnsureSpeakerReviewAsync(
        XamlRoot xamlRoot, LibraryService library, string bookId, uint currentSection, Action onChanged)
    {
        bool dark = ThemeHelper.IsDark(xamlRoot.Content is FrameworkElement fe ? fe.ActualTheme : ElementTheme.Light);
        var p = ThemeHelper.Palette(dark);
        var useTint = library.Prefs.Tint;
        var tint = ThemeHelper.TintColors(useTint, dark);
        var serifFont = new FontFamily(YYFontFamily.Serif.WindowsFamily());

        var (everyone, initialRaw) = await Task.Run(() =>
        {
            CastPick[] ev;
            try { ev = library.Core.DramaEveryone(bookId, currentSection); } catch { ev = []; }
            UnsureLine[] rw;
            try { rw = library.Core.DramaReview(bookId, currentSection); } catch { rw = []; }
            return (ev, rw);
        });
        var lookMap = everyone.GroupBy(x => x.Name).ToDictionary(g => g.Key, g => g.First());
        UnsureLine[] cachedRaw = initialRaw;

        int skipOffset = 0;
        int resolvedCount = 0;
        int settledTotal = 0;
        (uint Sec, uint Sent, uint Start, string Name, uint Freed)? lastDecision = null;
        UnsureLine? activeLine = null;
        int rebuildGen = 0;

        var outer = new StackPanel { Spacing = 10, Padding = new Thickness(18, 16, 18, 16) };
        ContentDialog? dlgRef = null;
        bool openPickerNext = false;

        async Task RefreshReviewAndRebuildAsync()
        {
            int gen = ++rebuildGen;
            var latest = await Task.Run(() =>
            {
                try { return library.Core.DramaReview(bookId, currentSection); }
                catch { return Array.Empty<UnsureLine>(); }
            });
            if (gen != rebuildGen) return;
            cachedRaw = latest;
            RebuildCard();
        }

        void CommitChoice(UnsureLine cur, UserChoice choice, string displayLabel)
        {
            uint freed = cur.Frees;
            activeLine = null;
            _ = Task.Run(() =>
            {
                try { library.Core.DramaSetSpeaker(bookId, cur.Section, cur.Sentence, cur.Start, choice); }
                catch { }
            }).ContinueWith(_ =>
            {
                outer.DispatcherQueue?.TryEnqueue(() =>
                {
                    lastDecision = (cur.Section, cur.Sentence, cur.Start, displayLabel, freed);
                    resolvedCount++;
                    settledTotal += (int)Math.Max(1u, freed);
                    DramaSynthService.InvalidateCast(bookId);
                    onChanged();
                    _ = RefreshReviewAndRebuildAsync();
                });
            }, TaskScheduler.Default);
        }

        void RebuildCard()
        {
            outer.Children.Clear();
            activeLine = null;

            outer.Children.Add(YYChrome.MakeSheetHeader("待确认的台词", null, () => dlgRef?.Hide(), dark));

            var ordered = cachedRaw
                .OrderByDescending(u => u.Section == currentSection)
                .ThenByDescending(u => u.Frees)
                .ToArray();

            if (lastDecision is var (lSec, lSent, lStart, lName, lFreed))
            {
                var undoRow = new Grid { ColumnSpacing = 8 };
                undoRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                undoRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var undoLeft = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
                undoLeft.Children.Add(L10n.Skip(new TextBlock
                {
                    Text = $"✓ {lName}",
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = ThemeHelper.Brush(tint.Accent),
                }));
                if (lFreed > 1)
                {
                    undoLeft.Children.Add(new TextBlock
                    {
                        Text = $"也定了 {lFreed} 句",
                        FontSize = 11,
                        Foreground = ThemeHelper.Brush(p.T3),
                        VerticalAlignment = VerticalAlignment.Center,
                    });
                }
                undoRow.Children.Add(undoLeft);

                var undoBtn = YYChrome.MakeSheetPillButton("撤销", () =>
                {
                    lastDecision = null;
                    resolvedCount = Math.Max(0, resolvedCount - 1);
                    settledTotal = Math.Max(0, settledTotal - (int)Math.Max(1u, lFreed));
                    _ = Task.Run(() =>
                    {
                        try { library.Core.DramaClearSpeaker(bookId, lSec, lSent, lStart); } catch { }
                    }).ContinueWith(_ =>
                    {
                        outer.DispatcherQueue?.TryEnqueue(() =>
                        {
                            DramaSynthService.InvalidateCast(bookId);
                            onChanged();
                            _ = RefreshReviewAndRebuildAsync();
                        });
                    }, TaskScheduler.Default);
                }, dark, useTint, kind: "secondary", height: 24, horizontalPadding: 10);
                Grid.SetColumn(undoBtn, 1);
                undoRow.Children.Add(undoBtn);

                outer.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(10),
                    Background = ThemeHelper.Brush(p.Card),
                    BorderBrush = ThemeHelper.Brush(p.Line),
                    BorderThickness = new Thickness(0.75),
                    Padding = new Thickness(12, 7, 10, 7),
                    Child = undoRow,
                });
            }

            if (ordered.Length == 0 || skipOffset >= ordered.Length)
            {
                var doneStack = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, Padding = new Thickness(12, 20, 12, 20) };
                var checkIcon = YYIcons.Make("checkmark", 22, ThemeHelper.Brush(tint.Accent));
                checkIcon.HorizontalAlignment = HorizontalAlignment.Center;
                doneStack.Children.Add(checkIcon);
                doneStack.Children.Add(new TextBlock
                {
                    Text = L10n.T("读到这里的台词都认好了"),
                    FontSize = 13.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = ThemeHelper.Brush(p.T1),
                    HorizontalAlignment = HorizontalAlignment.Center,
                });
                if (resolvedCount > 0)
                {
                    doneStack.Children.Add(new TextBlock
                    {
                        Text = $"点了 {resolvedCount} 下，带走 {settledTotal} 句",
                        FontSize = 11,
                        Foreground = ThemeHelper.Brush(p.T3),
                        HorizontalAlignment = HorizontalAlignment.Center,
                    });
                }
                outer.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(14),
                    Background = ThemeHelper.Brush(p.Card),
                    BorderBrush = ThemeHelper.Brush(p.Line),
                    BorderThickness = new Thickness(0.75),
                    Child = doneStack,
                });
                L10n.ApplyTree(outer);
                return;
            }

            var cur = ordered[skipOffset];
            activeLine = cur;

            var cardBody = new StackPanel { Spacing = 0 };

            // Top location + present characters line
            var whereParts = new List<string> { $"第 {cur.Section + 1} 章" };
            if (!string.IsNullOrWhiteSpace(cur.Place)) whereParts.Add(cur.Place);
            if (cur.Present is { Length: > 0 }) whereParts.Add("在场 " + string.Join(" ", cur.Present.Take(5)));
            whereParts.Add($"待确认 {ordered.Length - skipOffset}");
            cardBody.Children.Add(L10n.Skip(new TextBlock
            {
                Text = string.Join(" · ", whereParts),
                FontSize = 10.5,
                Foreground = ThemeHelper.Brush(p.T3),
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(14, 11, 14, 0),
            }));

            // Scene context with highlighted target quote (1:1 UnsureView.context)
            var ctxBlock = L10n.Skip(new TextBlock
            {
                FontFamily = serifFont,
                FontSize = 13.5,
                LineHeight = 23,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(14, 8, 14, 12),
            });
            var ctxArr = cur.Context ?? [];
            int relAt = (int)cur.Sentence - (int)cur.ContextFirst;
            if (ctxArr.Length > 0)
            {
                for (int i = 0; i < ctxArr.Length; i++)
                {
                    string s = ctxArr[i];
                    if (i == relAt && !string.IsNullOrEmpty(cur.Text) && s.IndexOf(cur.Text, StringComparison.Ordinal) is int hitIdx && hitIdx >= 0)
                    {
                        if (hitIdx > 0)
                            ctxBlock.Inlines.Add(new Run { Text = s[..hitIdx], Foreground = ThemeHelper.Brush(p.T3) });
                        ctxBlock.Inlines.Add(new Run
                        {
                            Text = cur.Text,
                            FontWeight = FontWeights.SemiBold,
                            Foreground = ThemeHelper.Brush(p.T1),
                        });
                        int afterPos = hitIdx + cur.Text.Length;
                        if (afterPos < s.Length)
                            ctxBlock.Inlines.Add(new Run { Text = s[afterPos..], Foreground = ThemeHelper.Brush(p.T3) });
                    }
                    else
                    {
                        ctxBlock.Inlines.Add(new Run { Text = s, Foreground = ThemeHelper.Brush(p.T3) });
                    }
                }
            }
            else
            {
                ctxBlock.Inlines.Add(new Run { Text = cur.Text, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
            }
            cardBody.Children.Add(ctxBlock);
            cardBody.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line) });

            var pickZone = new StackPanel { Spacing = 6, Padding = new Thickness(12, 10, 12, 12) };
            pickZone.Children.Add(new TextBlock
            {
                Text = L10n.T("这句是谁说的？"),
                FontSize = 11,
                Foreground = ThemeHelper.Brush(p.T3),
                Margin = new Thickness(2, 0, 0, 2),
            });

            string[] candNames = (cur.Names is { Length: > 0 } ? cur.Names : cur.Present) ?? [];
            for (int i = 0; i < Math.Min(candNames.Length, 3); i++)
            {
                string cand = candNames[i];
                bool topPick = i == 0;
                lookMap.TryGetValue(cand, out var look);
                Color avColor = ParseHexColor(look?.Color, tint.Accent);
                string avGlyph = look?.Avatar ?? (cand.Length > 0 ? cand[^1].ToString() : "?");
                string reason = i == 0 ? "最可能" : ((cur.Present?.Contains(cand) == true) ? "在场" : "也可能");

                var rowG = new Grid { ColumnSpacing = 9 };
                rowG.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                rowG.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                rowG.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                rowG.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                rowG.Children.Add(MakeDramaAvatar(avGlyph, avColor, 24));
                var nmTb = L10n.Skip(new TextBlock
                {
                    Text = cand,
                    FontSize = 12.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = ThemeHelper.Brush(p.T1),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                Grid.SetColumn(nmTb, 1);
                rowG.Children.Add(nmTb);

                var rsnTb = new TextBlock
                {
                    Text = L10n.T(reason),
                    FontSize = 10.5,
                    Foreground = ThemeHelper.Brush(p.T3),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(rsnTb, 2);
                rowG.Children.Add(rsnTb);

                var keycap = YYChrome.MakeKeycap($"{i + 1}", dark);
                Grid.SetColumn(keycap, 3);
                rowG.Children.Add(keycap);

                var candBtn = new Button
                {
                    Content = rowG,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Padding = new Thickness(10, 7, 10, 7),
                    CornerRadius = new CornerRadius(10),
                    Background = ThemeHelper.Brush(topPick ? tint.Soft : p.Bg),
                    BorderBrush = ThemeHelper.Brush(topPick ? tint.Edge : p.Line),
                    BorderThickness = new Thickness(0.75),
                    UseSystemFocusVisuals = false,
                };
                candBtn.Click += (_, _) => CommitChoice(cur, new UserChoice.Person(cand), cand);
                pickZone.Children.Add(candBtn);
            }

            var subActions = new Grid { ColumnSpacing = 6, Margin = new Thickness(0, 4, 0, 0) };
            subActions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            subActions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            subActions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            subActions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            subActions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var otherBtn = YYChrome.MakeSheetPillButton("别人…", () => { openPickerNext = true; dlgRef?.Hide(); }, dark, useTint, kind: "secondary", height: 26, horizontalPadding: 11);
            var notSpeechBtn = YYChrome.MakeSheetPillButton("不是台词", () => CommitChoice(cur, new UserChoice.NotSpeech(), "不是台词"), dark, useTint, kind: "secondary", height: 26, horizontalPadding: 11);
            var grpBtn = YYChrome.MakeSheetPillButton("众人", () => CommitChoice(cur, new UserChoice.Group("众人"), "众人"), dark, useTint, kind: "secondary", height: 26, horizontalPadding: 11);
            Grid.SetColumn(notSpeechBtn, 1);
            Grid.SetColumn(grpBtn, 2);

            var skipBtn = new Button
            {
                Content = L10n.T("跳过 →"),
                Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
                Foreground = ThemeHelper.Brush(p.T3),
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };
            skipBtn.Click += (_, _) =>
            {
                lastDecision = null;
                skipOffset = ordered.Length > 0 ? (skipOffset + 1) % ordered.Length : 0;
                RebuildCard();
            };
            Grid.SetColumn(skipBtn, 4);

            subActions.Children.Add(otherBtn);
            subActions.Children.Add(notSpeechBtn);
            subActions.Children.Add(grpBtn);
            subActions.Children.Add(skipBtn);
            pickZone.Children.Add(subActions);
            cardBody.Children.Add(pickZone);

            outer.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(14),
                Background = ThemeHelper.Brush(p.Card),
                BorderBrush = ThemeHelper.Brush(p.Line),
                BorderThickness = new Thickness(0.75),
                Child = cardBody,
            });

            if (cur.Frees > 1)
            {
                outer.Children.Add(new TextBlock
                {
                    Text = $"定了这句，后面 {cur.Frees} 句也跟着定。",
                    FontSize = 10.5,
                    Foreground = ThemeHelper.Brush(p.T3),
                    Margin = new Thickness(2, 0, 0, 0),
                });
            }

            L10n.ApplyTree(outer);
        }

        while (true)
        {
            openPickerNext = false;
            RebuildCard();

            dlgRef = YYChrome.CreateMacSheet(xamlRoot, dark, 400, outer, maxHeight: 640, tint: useTint);
            dlgRef.PreviewKeyDown += (_, e) =>
            {
                if (activeLine is null) return;
                bool ctrl = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
                bool alt = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
                if (ctrl || alt) return;
                string[] names = (activeLine.Names is { Length: > 0 } ? activeLine.Names : activeLine.Present) ?? [];
                int idx = e.Key switch
                {
                    VirtualKey.Number1 or VirtualKey.NumberPad1 => 0,
                    VirtualKey.Number2 or VirtualKey.NumberPad2 => 1,
                    VirtualKey.Number3 or VirtualKey.NumberPad3 => 2,
                    _ => -1,
                };
                if (idx >= 0 && idx < names.Length)
                {
                    e.Handled = true;
                    CommitChoice(activeLine, new UserChoice.Person(names[idx]), names[idx]);
                }
            };
            await dlgRef.ShowSafeAsync(useTint);

            if (openPickerNext && activeLine is not null)
            {
                var targetLine = activeLine;
                var chosen = await ShowPeoplePickerDialogAsync(xamlRoot, library, bookId, currentSection, "是谁说的");
                if (chosen is not null)
                {
                    string lbl = chosen switch
                    {
                        UserChoice.Person pr => pr.Name,
                        UserChoice.Group gr => gr.Name,
                        _ => "不是台词",
                    };
                    try { await Task.Run(() => library.Core.DramaSetSpeaker(bookId, targetLine.Section, targetLine.Sentence, targetLine.Start, chosen)); }
                    catch { }
                    lastDecision = (targetLine.Section, targetLine.Sentence, targetLine.Start, lbl, targetLine.Frees);
                    resolvedCount++;
                    settledTotal += (int)Math.Max(1u, targetLine.Frees);
                    DramaSynthService.InvalidateCast(bookId);
                    onChanged();
                    cachedRaw = await Task.Run(() =>
                    {
                        try { return library.Core.DramaReview(bookId, currentSection); }
                        catch { return Array.Empty<UnsureLine>(); }
                    });
                }
                continue;
            }
            break;
        }
    }

    // ---------- 5. Role Detail Sheet (1:1 CastView.swift RoleView: 380px CreateMacSheet, real book lines & debounced sliders) ----------

    public static async Task ShowRoleDetailDialogAsync(
        Window ownerWindow, XamlRoot xamlRoot, LibraryService library, string bookId, string roleName,
        Func<string, string?, Action<string>, Task> openVoicePicker, Action onChanged)
    {
        bool dark = ThemeHelper.IsDark(xamlRoot.Content is FrameworkElement fe ? fe.ActualTheme : ElementTheme.Light);
        var p = ThemeHelper.Palette(dark);
        var useTint = library.Prefs.Tint;
        var tint = ThemeHelper.TintColors(useTint, dark);
        var serifFont = new FontFamily(YYFontFamily.Serif.WindowsFamily());

        string bookLang = "zh-Hans";
        try { bookLang = library.GetBookDetail(bookId).Summary.Language ?? "zh-Hans"; } catch { }

        string currentRole = roleName;
        var outer = new StackPanel { Spacing = 12, Padding = new Thickness(18, 16, 18, 16) };
        string? pendingAction = null;
        ContentDialog? roleDlg = null;

        double curRate = 1.0;
        double curPitch = 0.0;
        bool curRight = false;
        string? curVid = null;
        bool dirtySliders = false;

        var saveDebounce = new DispatcherTimer { Interval = System.TimeSpan.FromMilliseconds(220) };
        void FlushRoleSave()
        {
            if (!dirtySliders) return;
            dirtySliders = false;
            try
            {
                library.Core.SetBookRole(bookId, currentRole, new RoleVoice(curVid, curRate, curPitch, curRight));
                DramaSynthService.InvalidateCast(bookId);
                onChanged();
            }
            catch { }
        }
        saveDebounce.Tick += (_, _) =>
        {
            saveDebounce.Stop();
            FlushRoleSave();
        };

        async Task PlayTunedRoleAuditionAsync(string sampleQuote)
        {
            string? sampleVoice = curVid;
            if (string.IsNullOrEmpty(sampleVoice))
            {
                try { sampleVoice = DramaSynthService.NormalizeVoiceId(library.Core.DramaRoleVoices(bookId).FirstOrDefault(v => v.Name == currentRole)?.Voice); }
                catch { }
            }
            if (string.IsNullOrEmpty(sampleVoice))
                sampleVoice = DramaSynthService.NormalizeVoiceId(library.Prefs.GetVoiceForLanguage(bookLang));

            if (Math.Abs(curPitch) < 0.05)
            {
                await AudioPlayer.PreviewVoiceAsync(library, sampleVoice, library.Prefs.SpeechRate * curRate, sampleQuote);
                return;
            }

            // Pitch-tuned preview via YuyueCoreMethods.TuneSpeechPcm + PlayWavBytesOnceAsync
            try
            {
                using var synthHelper = new AudioPlayer(library, sampleVoice);
                synthHelper.SetBookContext(bookId, bookLang);
                byte[]? baseWav = await synthHelper.SynthesizeClipForDramaAsync(sampleQuote, sampleQuote, sampleVoice, null, CancellationToken.None);
                if (baseWav is { Length: > 44 })
                {
                    var (samples, sr) = AudioPlayer.DecodeWavPcm16Mono(baseWav);
                    if (samples.Length > 0)
                    {
                        var tuned = YuyueCoreMethods.TuneSpeechPcm(samples, sr, (float)Math.Clamp(curRate * library.Prefs.SpeechRate, 0.5, 2.0), (float)curPitch);
                        byte[] outBytes = new byte[tuned.Length * 2];
                        System.Buffer.BlockCopy(tuned, 0, outBytes, 0, outBytes.Length);
                        byte[] tunedWav = CloudVoiceService.WrapPcm16MonoInWav(outBytes, (int)sr);
                        await PlayWavBytesOnceAsync(tunedWav);
                        return;
                    }
                }
            }
            catch { }

            await AudioPlayer.PreviewVoiceAsync(library, sampleVoice, library.Prefs.SpeechRate * curRate, sampleQuote);
        }

        int roleLoadGen = 0;

        async Task RebuildRoleAsync()
        {
            int gen = ++roleLoadGen;
            string roleSnap = currentRole;
            var loaded = await Task.Run(() =>
            {
                string[] al;
                CharacterGender gd;
                RoleVoice? rv = null;
                CastMember? cm = null;
                bool dr = false;
                try
                {
                    uint curSec = library.GetProgress(bookId)?.Position.Section ?? 0;
                    al = library.Core.DramaNames(bookId, roleSnap);
                    gd = library.Core.DramaGender(bookId, roleSnap);
                    rv = library.Core.BookRoles(bookId).FirstOrDefault(r => r.Name == roleSnap)?.Voice;
                    cm = library.Core.BookCast(bookId, curSec).FirstOrDefault(m => m.Name == roleSnap);
                    dr = library.Core.DramaEnabled(bookId);
                }
                catch
                {
                    al = [roleSnap];
                    gd = CharacterGender.Unknown;
                }
                if (al.Length == 0) al = [roleSnap];
                string quote = FindCharacterSampleLine(library, bookId, roleSnap, bookLang);
                var voices = AudioPlayer.GetAllVoiceOptions(library, bookId);
                return (al, gd, rv, cm, dr, quote, voices);
            });
            if (gen != roleLoadGen) return;

            outer.Children.Clear();
            var (aliases, gender, curRoleVoice, castMember, dramaOn, sampleQuote, allV) = loaded;

            curRate = curRoleVoice?.Rate ?? 1.0;
            curPitch = curRoleVoice?.Pitch ?? 0.0;
            curRight = curRoleVoice?.Right ?? false;
            curVid = DramaSynthService.NormalizeVoiceId(curRoleVoice?.Voice);

            Color avColor = ParseHexColor(castMember?.Color, tint.Accent);
            string avGlyph = castMember?.Avatar ?? (currentRole.Length > 0 ? currentRole[^1].ToString() : "?");
            string? lineCountSub = castMember is not null ? $"{castMember.LinesSoFar} 句" : null;

            outer.Children.Add(YYChrome.MakeSheetHeader(currentRole, lineCountSub, () => roleDlg?.Hide(), dark, MakeDramaAvatar(avGlyph, avColor, 30)));

            // 1. Names & Gender card (1:1 RoleNamesSection)
            var namesCard = new StackPanel { Spacing = 8 };
            namesCard.Children.Add(new TextBlock { Text = L10n.T("书里怎么叫"), FontSize = 11, Foreground = ThemeHelper.Brush(p.T3) });

            var chipsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            for (int i = 0; i < aliases.Length; i++)
            {
                string al = aliases[i];
                bool primary = i == 0;
                var chipInner = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
                var mainBtn = L10n.Skip(new Button
                {
                    Content = al,
                    Padding = new Thickness(7, 2, primary ? 7 : 3, 2),
                    MinHeight = 22,
                    Height = 22,
                    FontSize = 11.5,
                    Background = new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    Foreground = ThemeHelper.Brush(primary ? tint.Accent : p.T1),
                    FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal,
                    UseSystemFocusVisuals = false,
                });
                mainBtn.Click += (_, _) =>
                {
                    if (!primary)
                    {
                        try
                        {
                            FlushRoleSave();
                            library.Core.DramaEditCast(bookId, new CastEdit.Show(al));
                            currentRole = al;
                            DramaSynthService.InvalidateCast(bookId);
                            onChanged();
                            _ = RebuildRoleAsync();
                        }
                        catch { }
                    }
                };
                chipInner.Children.Add(mainBtn);

                if (!primary)
                {
                    var splitBtn = new Button
                    {
                        Content = "×",
                        Padding = new Thickness(3, 0, 5, 0),
                        MinHeight = 22,
                        Height = 22,
                        FontSize = 11,
                        Foreground = ThemeHelper.Brush(p.T3),
                        Background = new SolidColorBrush(Colors.Transparent),
                        BorderThickness = new Thickness(0),
                        UseSystemFocusVisuals = false,
                    };
                    splitBtn.Click += (_, _) =>
                    {
                        try
                        {
                            library.Core.DramaEditCast(bookId, new CastEdit.Split(al));
                            DramaSynthService.InvalidateCast(bookId);
                            onChanged();
                            _ = RebuildRoleAsync();
                        }
                        catch { }
                    };
                    chipInner.Children.Add(splitBtn);
                }

                chipsRow.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(7),
                    Background = ThemeHelper.Brush(primary ? tint.Soft : p.Bg),
                    BorderBrush = ThemeHelper.Brush(primary ? tint.Edge : p.Line),
                    BorderThickness = new Thickness(0.5),
                    Child = chipInner,
                });
            }

            var addAliasBtn = new Button
            {
                Content = "＋",
                Height = 22,
                MinHeight = 22,
                Padding = new Thickness(8, 0, 8, 0),
                CornerRadius = new CornerRadius(7),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderBrush = ThemeHelper.Brush(p.Line),
                BorderThickness = new Thickness(0.75),
                Foreground = ThemeHelper.Brush(p.T2),
                FontSize = 11.5,
                UseSystemFocusVisuals = false,
            };
            addAliasBtn.Click += (_, _) => { pendingAction = "add-alias"; roleDlg?.Hide(); };
            chipsRow.Children.Add(addAliasBtn);

            namesCard.Children.Add(new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = chipsRow,
            });

            namesCard.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(0, 2, 0, 0) });

            var genderGrid = new Grid();
            genderGrid.Children.Add(new TextBlock { Text = L10n.T("角色性别"), FontSize = 12, Foreground = ThemeHelper.Brush(p.T1), VerticalAlignment = VerticalAlignment.Center });
            string gActive = gender switch { CharacterGender.M => "m", CharacterGender.F => "f", _ => "u" };
            var gSeg = YYChrome.MakeSegmentedBar(
                [("m", L10n.T("男")), ("f", L10n.T("女")), ("u", L10n.T("不知道"))],
                gActive,
                sel =>
                {
                    var nextG = sel switch { "m" => CharacterGender.M, "f" => CharacterGender.F, _ => CharacterGender.Unknown };
                    try { library.Core.DramaEditCast(bookId, new CastEdit.SetGender(currentRole, nextG)); } catch { }
                    DramaSynthService.InvalidateCast(bookId);
                    onChanged();
                    _ = RebuildRoleAsync();
                },
                dark,
                fontSize: 11.5,
                height: 27);
            gSeg.HorizontalAlignment = HorizontalAlignment.Right;
            genderGrid.Children.Add(gSeg);
            namesCard.Children.Add(genderGrid);

            outer.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(12),
                Background = ThemeHelper.Brush(p.Card),
                BorderBrush = ThemeHelper.Brush(p.Line),
                BorderThickness = new Thickness(0.75),
                Padding = new Thickness(12, 10, 12, 10),
                Child = namesCard,
            });

            // 2. Voice & Bubble Side card
            string voiceDisplay = L10n.T(dramaOn ? "自动选角" : "跟旁白");
            if (!string.IsNullOrEmpty(curVid))
            {
                voiceDisplay = allV.FirstOrDefault(v => v.Id == curVid)?.Label.Split('（')[0].Trim('✦', '☁', ' ') ?? curVid;
            }

            var voiceCard = new StackPanel { Spacing = 8 };
            var vRow = new Grid { ColumnSpacing = 6 };
            vRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            vRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            vRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var vMeta = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            vMeta.Children.Add(new TextBlock { Text = L10n.T("声音"), FontSize = 12, FontWeight = FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1) });
            vMeta.Children.Add(L10n.Skip(new TextBlock { Text = voiceDisplay, FontSize = 11, Foreground = ThemeHelper.Brush(string.IsNullOrEmpty(curVid) ? p.T3 : tint.Accent), TextTrimming = TextTrimming.CharacterEllipsis }));
            vRow.Children.Add(vMeta);

            var pickBtn = YYChrome.MakeSheetPillButton("换声音…", () => { FlushRoleSave(); pendingAction = "pick"; roleDlg?.Hide(); }, dark, useTint, kind: "warm", height: 27, horizontalPadding: 11);
            var cloneBtn = YYChrome.MakeSheetPillButton("录声音…", () => { FlushRoleSave(); pendingAction = "clone"; roleDlg?.Hide(); }, dark, useTint, kind: "secondary", height: 27, horizontalPadding: 11);
            Grid.SetColumn(pickBtn, 1);
            Grid.SetColumn(cloneBtn, 2);
            vRow.Children.Add(pickBtn);
            vRow.Children.Add(cloneBtn);
            voiceCard.Children.Add(vRow);

            voiceCard.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line) });

            var sideRow = new Grid();
            sideRow.Children.Add(new TextBlock { Text = L10n.T("气泡放在"), FontSize = 12, Foreground = ThemeHelper.Brush(p.T1), VerticalAlignment = VerticalAlignment.Center });
            var sideSeg = YYChrome.MakeSegmentedBar(
                [("left", L10n.T("左边")), ("right", L10n.T("右边"))],
                curRight ? "right" : "left",
                sel =>
                {
                    curRight = sel == "right";
                    try
                    {
                        library.Core.SetBookRole(bookId, currentRole, new RoleVoice(curVid, curRate, curPitch, curRight));
                        onChanged();
                    }
                    catch { }
                    _ = RebuildRoleAsync();
                },
                dark,
                fontSize: 11.5,
                height: 27);
            sideSeg.HorizontalAlignment = HorizontalAlignment.Right;
            sideRow.Children.Add(sideSeg);
            voiceCard.Children.Add(sideRow);

            outer.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(12),
                Background = ThemeHelper.Brush(p.Card),
                BorderBrush = ThemeHelper.Brush(p.Line),
                BorderThickness = new Thickness(0.75),
                Padding = new Thickness(12, 10, 12, 10),
                Child = voiceCard,
            });

            // 3. Debounced Rate & Pitch card
            var slidersCard = new StackPanel { Spacing = 10 };
            var rateHeaderRow = new Grid();
            var rateValLabel = new TextBlock { Text = $"{L10n.T("快慢")} {curRate:0.1}×", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) };
            rateHeaderRow.Children.Add(rateValLabel);
            rateHeaderRow.Children.Add(new TextBlock { Text = L10n.T("在全书速度上再快或慢一点"), FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom });
            slidersCard.Children.Add(rateHeaderRow);

            var rateSlider = new Slider { Minimum = 0.7, Maximum = 1.4, StepFrequency = 0.05, Value = Math.Clamp(curRate, 0.7, 1.4) };
            rateSlider.ValueChanged += (_, e) =>
            {
                double next = Math.Round(e.NewValue, 2);
                if (Math.Abs(next - curRate) < 0.005) return;
                curRate = next;
                rateValLabel.Text = $"{L10n.T("快慢")} {curRate:0.1}×";
                dirtySliders = true;
                saveDebounce.Stop();
                saveDebounce.Start();
            };
            slidersCard.Children.Add(rateSlider);

            var pitchHeaderRow = new Grid();
            string FormatPitch(double v) => Math.Abs(v) < 0.05 ? "0" : $"{v:+0.#;-0.#;0}";
            var pitchValLabel = new TextBlock { Text = $"{L10n.T("高低")} {FormatPitch(curPitch)}", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) };
            pitchHeaderRow.Children.Add(pitchValLabel);
            pitchHeaderRow.Children.Add(new TextBlock { Text = L10n.T("声音高一点或低一点"), FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom });
            slidersCard.Children.Add(pitchHeaderRow);

            var pitchSlider = new Slider { Minimum = -6.0, Maximum = 6.0, StepFrequency = 1.0, Value = Math.Clamp(curPitch, -6.0, 6.0) };
            pitchSlider.ValueChanged += (_, e) =>
            {
                double next = Math.Round(e.NewValue);
                if (Math.Abs(next - curPitch) < 0.05) return;
                curPitch = next;
                pitchValLabel.Text = $"{L10n.T("高低")} {FormatPitch(curPitch)}";
                dirtySliders = true;
                saveDebounce.Stop();
                saveDebounce.Start();
            };
            slidersCard.Children.Add(pitchSlider);

            outer.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(12),
                Background = ThemeHelper.Brush(p.Card),
                BorderBrush = ThemeHelper.Brush(p.Line),
                BorderThickness = new Thickness(0.75),
                Padding = new Thickness(12, 10, 12, 10),
                Child = slidersCard,
            });

            // 4. Real character line audition card (1:1 RoleView:387-401)
            var audGrid = new Grid { ColumnSpacing = 10 };
            audGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            audGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            audGrid.Children.Add(new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(14),
                Background = ThemeHelper.Brush(p.Inset),
                VerticalAlignment = VerticalAlignment.Center,
                Child = YYIcons.Make("play", 10.5, ThemeHelper.Brush(p.T1), filled: true),
            });
            var audTextCol = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            audTextCol.Children.Add(L10n.Skip(new TextBlock
            {
                Text = $"“{sampleQuote}”",
                FontFamily = serifFont,
                FontSize = 12.5,
                Foreground = ThemeHelper.Brush(p.T1),
                MaxLines = 2,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.Wrap,
            }));
            audTextCol.Children.Add(L10n.Skip(new TextBlock
            {
                Text = $"{currentRole}的一句 · 点一下试听",
                FontSize = 10.5,
                Foreground = ThemeHelper.Brush(p.T3),
            }));
            Grid.SetColumn(audTextCol, 1);
            audGrid.Children.Add(audTextCol);

            var audCardBtn = new Button
            {
                Content = audGrid,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(12, 10, 12, 10),
                CornerRadius = new CornerRadius(12),
                Background = ThemeHelper.Brush(p.Card),
                BorderBrush = ThemeHelper.Brush(p.Line),
                BorderThickness = new Thickness(0.75),
                UseSystemFocusVisuals = false,
            };
            audCardBtn.Click += async (_, _) => await PlayTunedRoleAuditionAsync(sampleQuote);
            outer.Children.Add(audCardBtn);

            if (!string.IsNullOrEmpty(curVid) || Math.Abs(curRate - 1.0) > 0.02 || Math.Abs(curPitch) > 0.05 || curRight)
            {
                var resetBtn = YYChrome.MakeSheetPillButton(dramaOn ? "恢复自动选角" : "改回跟旁白", () =>
                {
                    saveDebounce.Stop();
                    dirtySliders = false;
                    try
                    {
                        library.Core.SetBookRole(bookId, currentRole, new RoleVoice(null, 1.0, 0.0, false));
                        DramaSynthService.InvalidateCast(bookId);
                        onChanged();
                        _ = RebuildRoleAsync();
                    }
                    catch { }
                }, dark, useTint, kind: "secondary", height: 28, horizontalPadding: 14);
                outer.Children.Add(resetBtn);
            }

            L10n.ApplyTree(outer);
        }

        while (true)
        {
            pendingAction = null;
            await RebuildRoleAsync();
            var scroll = new ScrollViewer { MaxHeight = 560, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = outer };
            roleDlg = YYChrome.CreateMacSheet(xamlRoot, dark, 380, scroll, maxHeight: 620, tint: useTint);
            await roleDlg.ShowSafeAsync(useTint);

            saveDebounce.Stop();
            FlushRoleSave();
            StopClonePreview();
            AudioPlayer.StopActivePreview();

            if (pendingAction == "add-alias")
            {
                string? added = await YYChrome.ShowPromptAsync(
                    xamlRoot, dark, $"书里还怎么叫{currentRole}", placeholder: "名字", confirmText: "加上", tint: useTint);
                if (!string.IsNullOrWhiteSpace(added))
                {
                    try
                    {
                        library.Core.DramaEditCast(bookId, new CastEdit.AddName(currentRole, added.Trim()));
                        DramaSynthService.InvalidateCast(bookId);
                        onChanged();
                    }
                    catch { }
                }
                continue;
            }
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
