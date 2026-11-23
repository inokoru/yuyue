using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
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

/// <summary>
/// 1:1 port of <c>CloneVoiceView.swift</c>, <c>ClonedVoice.swift</c>, <c>DramaWork.swift</c>, and <c>DramaViews.swift</c>:
/// - Consent-gated 44.1 kHz Microphone Voice Cloning &amp; audio file import with <see cref="YuyueCore.CardCheck"/> quality gate,
///   <see cref="YuyueCore.CardSave"/> registration, optional ElevenLabs Instant Clone upload, and cloned card management;
/// - Background LLM Speaker Recognition worker (<see cref="YuyueCore.DramaNextTask"/> / <see cref="YuyueCore.DramaSubmit"/>)
///   with exponential backoff, active-learning <c>UnsureView</c> dialog, character alias/gender editor, and merge suggestions.
/// </summary>
public static class VoiceCloneAndDramaDialogs
{
    private static readonly HttpClient Http = new() { Timeout = System.TimeSpan.FromSeconds(60) };
    private static readonly Dictionary<string, CancellationTokenSource> ActiveDramaWorkers = new();

    // ---------- 1. Voice Cloning Modal (1:1 CloneVoiceView.swift) ----------

    public static async Task<VoiceCard?> ShowCloneVoiceDialogAsync(
        Window ownerWindow, XamlRoot xamlRoot, LibraryService library, string? forBookId = null, string? forRoleName = null)
    {
        bool dark = ThemeHelper.IsDark(xamlRoot.Content is FrameworkElement fe ? fe.ActualTheme : ElementTheme.Light);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(library.Prefs.Tint, dark);

        string lang = "zh";
        string scriptText;
        try { scriptText = library.Core.CardScript(lang, CardStyle.Neutral); }
        catch { scriptText = "春天的风轻轻吹过窗台，翻开书页的声音格外安静，每一句话都带着温度。"; }

        bool consented = false;
        float[]? capturedSamples24k = null;
        CardCheck? latestCheck = null;
        VoiceCard? savedCard = null;

        MediaCapture? capture = null;
        InMemoryRandomAccessStream? recStream = null;
        bool isRecording = false;

        var nameBox = new TextBox { Header = "给这个声音起个名字", PlaceholderText = forRoleName is not null ? $"{forRoleName}的声音" : "例如：我的声音", Text = forRoleName ?? "" };
        var scriptBox = new TextBox
        {
            Header = "朗读参考文本（录 6–15 秒清晰人声）",
            Text = scriptText,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
        };

        var consentCheck = new CheckBox
        {
            Content = "这是我的声音，或者我得到了本人同意",
            FontSize = 12,
        };

        var recBtn = new Button { Style = (Style)Application.Current.Resources["YYPrimaryPillButtonStyle"], Content = "🎙 开始录音", IsEnabled = false };
        var importBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "导入一段录音…", IsEnabled = false };
        var useElevenCheck = new CheckBox
        {
            Content = "同时上传到 ElevenLabs 生成云端克隆音色",
            IsChecked = library.CloudVoices.IsConfigured(CloudTtsProvider.ElevenLabs),
            Visibility = library.CloudVoices.IsConfigured(CloudTtsProvider.ElevenLabs) ? Visibility.Visible : Visibility.Collapsed,
            FontSize = 11.5,
        };

        var checkCardHost = new StackPanel { Spacing = 4 };

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
        }

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
                    recBtn.Content = "⏹ 停止录音并检测";
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
                        finally
                        {
                            try { File.Delete(tmpWav); } catch { }
                        }
                    }
                }
                catch (Exception ex)
                {
                    checkCardHost.Children.Clear();
                    checkCardHost.Children.Add(new TextBlock { Text = $"处理录音失败：{ex.Message}", FontSize = 11.5, Foreground = ThemeHelper.Brush(p.Danger) });
                }
                finally
                {
                    recStream?.Dispose();
                    recStream = null;
                    capture?.Dispose();
                    capture = null;
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

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { recBtn, importBtn } };
        var root = new StackPanel
        {
            Width = 420,
            Spacing = 10,
            Children =
            {
                nameBox,
                scriptBox,
                consentCheck,
                btnRow,
                checkCardHost,
                useElevenCheck,
            },
        };

        var dlg = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "克隆一段声音",
            Content = root,
            PrimaryButtonText = "保存声音卡",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        var res = await dlg.ShowAsync();
        if (isRecording && capture is not null)
        {
            try { await capture.StopRecordAsync(); } catch { }
        }
        recStream?.Dispose();
        capture?.Dispose();

        if (res != ContentDialogResult.Primary || capturedSamples24k is null || capturedSamples24k.Length < 24_000)
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
                Lang: "zh",
                Origin: new CardOrigin.Cloned(),
                Clips: [new CardClip(CardStyle.Neutral, "neutral.wav", spokenTxt, secs)],
                Description: "本机克隆声音",
                Version: 1,
                Narrator: true,
                Rate: avgRate,
                Embedding: [],
                Sync: false);

            savedCard = await Task.Run(() => library.Core.CardSave(null, cardDraft, stageDir));

            // Optional ElevenLabs Instant Voice Clone upload (1:1 ClonedVoice.swift)
            if (useElevenCheck.IsChecked == true && library.CloudVoices.IsConfigured(CloudTtsProvider.ElevenLabs))
            {
                try
                {
                    string elKey = library.Secrets.Get("tts.elevenlabs.key").Trim();
                    using var form = new MultipartFormDataContent();
                    form.Add(new StringContent($"语阅 · {cardName}"), "name");
                    form.Add(new StringContent("true"), "remove_background_noise");
                    var fileBytes = await File.ReadAllBytesAsync(wavFile);
                    var filePart = new ByteArrayContent(fileBytes);
                    filePart.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
                    form.Add(filePart, "files", "neutral.wav");

                    using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.elevenlabs.io/v1/voices/add") { Content = form };
                    req.Headers.Add("xi-api-key", elKey);
                    using var resp = await Http.SendAsync(req);
                    if (resp.IsSuccessStatusCode)
                    {
                        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                        if (doc.RootElement.TryGetProperty("voice_id", out var vp) && vp.GetString() is string rvid)
                        {
                            library.Prefs.ClonedElevenLabsVoiceIds[savedCard.Id] = rvid;
                            library.SavePrefs();
                        }
                    }
                }
                catch { }
            }

            if (forBookId is not null && forRoleName is not null)
            {
                try { library.Core.SetBookRole(forBookId, forRoleName, new RoleVoice("card:" + savedCard.Id, 1.0, 0.0, false)); }
                catch { }
            }
            App.CurrentApp.BroadcastVoiceChanged();
            return savedCard;
        }
        catch (Exception ex)
        {
            await new ContentDialog { XamlRoot = xamlRoot, Title = "保存克隆声音失败", Content = ex.Message, CloseButtonText = "好" }.ShowAsync();
            return null;
        }
    }

    // ---------- 2. Background LLM Speaker Recognition Worker (1:1 DramaWork.swift) ----------

    public static bool IsDramaWorkerRunning(string bookId) => ActiveDramaWorkers.ContainsKey(bookId);

    public static void StopDramaWorker(string bookId)
    {
        if (ActiveDramaWorkers.TryGetValue(bookId, out var cts))
        {
            cts.Cancel();
            ActiveDramaWorkers.Remove(bookId);
        }
    }

    public static void StartDramaSpeakerWorker(
        LibraryService library, string bookId, uint focusSection, Action onProgressUpdated)
    {
        if (ActiveDramaWorkers.ContainsKey(bookId) || !library.Companion.IsConfigured) return;

        var cts = new CancellationTokenSource();
        ActiveDramaWorkers[bookId] = cts;
        var ct = cts.Token;

        try
        {
            library.Core.DramaSetCaps(new DramaCaps(
                Llm: true,
                VerifyDistinct: false,
                ContextTokens: 120_000,
                Tts: library.LiveVoice.IsReady,
                DesignVoice: false,
                Sfx: false,
                Combo: $"{library.Prefs.AiProvider}/{library.Prefs.AiModel}",
                Engine: library.LiveVoice.IsReady ? "zipvoice-1" : "sapi",
                TtsSameEachTime: !library.LiveVoice.IsReady));
        }
        catch { }

        _ = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    DramaTask? task;
                    try { task = library.Core.DramaNextTask(bookId, focusSection); }
                    catch { break; }

                    if (task is null) break;

                    if (task is DramaTask.Llm llm)
                    {
                        string prompt = string.IsNullOrWhiteSpace(llm.Schema)
                            ? llm.User
                            : $"{llm.User}\n\n<schema>\n{llm.Schema}\n</schema>";

                        string? answer = null;
                        int[] backoffs = [4, 12, 36];
                        for (int attempt = 0; attempt <= backoffs.Length; attempt++)
                        {
                            ct.ThrowIfCancellationRequested();
                            try
                            {
                                answer = await library.Companion.ChatAsync(prompt, llm.System);
                                break;
                            }
                            catch when (attempt < backoffs.Length)
                            {
                                await Task.Delay(System.TimeSpan.FromSeconds(backoffs[attempt]), ct);
                            }
                            catch
                            {
                                answer = null;
                                break;
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(answer))
                        {
                            library.Core.DramaSubmit(bookId, llm.Id, new DramaResult.Llm(answer));
                            onProgressUpdated();
                        }
                        else
                        {
                            library.Core.DramaSubmit(bookId, llm.Id, new DramaResult.Returned());
                            break;
                        }
                    }
                    else
                    {
                        string tid = task switch
                        {
                            DramaTask.Tts t => t.Id,
                            _ => "",
                        };
                        if (!string.IsNullOrEmpty(tid))
                            library.Core.DramaSubmit(bookId, tid, new DramaResult.Returned());
                        break;
                    }
                }
            }
            catch { }
            finally
            {
                ActiveDramaWorkers.Remove(bookId);
                onProgressUpdated();
            }
        }, ct);
    }

    // ---------- 3. Active-Learning Unsure Speaker Review & Role Editor (1:1 DramaViews.swift) ----------

    public static async Task ShowUnsureSpeakerReviewAsync(
        XamlRoot xamlRoot, LibraryService library, string bookId, uint currentSection, Action onChanged)
    {
        bool dark = ThemeHelper.IsDark(xamlRoot.Content is FrameworkElement fe ? fe.ActualTheme : ElementTheme.Light);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(library.Prefs.Tint, dark);

        int skipOffset = 0;
        (uint Sec, uint Sent, uint Start, string Name, uint Freed)? lastDecision = null;
        var host = new StackPanel { Width = 440, Spacing = 10 };
        ContentDialog? dlgRef = null;

        void RebuildCard()
        {
            host.Children.Clear();
            UnsureLine[] raw;
            try { raw = library.Core.DramaReview(bookId, currentSection + 5); }
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
                    Text = lFreed > 0 ? $"✓ {lName} · 连带确定了后面 {lFreed} 句" : $"✓ 已标为 {lName}",
                    FontSize = 11.5,
                    Foreground = ThemeHelper.Brush(p.Ok),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                var uBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "撤销", Height = 24, MinHeight = 24, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right };
                uBtn.Click += (_, _) =>
                {
                    try { library.Core.DramaClearSpeaker(bookId, lSec, lSent, lStart); } catch { }
                    lastDecision = null;
                    onChanged();
                    RebuildCard();
                };
                undoRow.Children.Add(uBtn);
                host.Children.Add(undoRow);
            }

            if (ordered.Length == 0)
            {
                host.Children.Add(new TextBlock { Text = "✓ 当前章节没有待确认的台词归属了。", FontSize = 13, Foreground = ThemeHelper.Brush(p.T1), Margin = new Thickness(0, 12, 0, 12) });
                return;
            }

            var line = ordered[skipOffset % ordered.Length];
            string placeInfo = !string.IsNullOrEmpty(line.Place) ? $" · {line.Place}" : "";
            string presentInfo = line.Present is { Length: > 0 } ? $" · 在场：{string.Join("、", line.Present.Take(4))}" : "";
            host.Children.Add(new TextBlock { Text = $"第 {line.Section + 1} 章{placeInfo}{presentInfo}", FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), TextWrapping = TextWrapping.Wrap });

            // Context card highlighting target line in yellow
            var ctxTb = new TextBlock { FontFamily = new FontFamily(YYFontFamily.Serif.WindowsFamily()), FontSize = 13, LineHeight = 21, TextWrapping = TextWrapping.Wrap, Foreground = ThemeHelper.Brush(p.T1) };
            string joinedCtx = line.Context.Length > 0 ? string.Join("\n", line.Context) : line.Text;
            int hitIdx = joinedCtx.IndexOf(line.Text, StringComparison.Ordinal);
            if (hitIdx >= 0)
            {
                if (hitIdx > 0) ctxTb.Inlines.Add(new Run { Text = joinedCtx[..hitIdx], Foreground = ThemeHelper.Brush(p.T2) });
                ctxTb.Inlines.Add(new Run { Text = line.Text, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(tint.Accent) });
                int after = hitIdx + line.Text.Length;
                if (after < joinedCtx.Length) ctxTb.Inlines.Add(new Run { Text = joinedCtx[after..], Foreground = ThemeHelper.Brush(p.T2) });
            }
            else
            {
                ctxTb.Text = joinedCtx;
            }

            host.Children.Add(new Border { CornerRadius = new CornerRadius(10), Background = ThemeHelper.Brush(p.Inset), Padding = new Thickness(12, 10, 12, 10), Child = ctxTb });

            if (line.Frees > 0)
                host.Children.Add(new TextBlock { Text = $"✦ 定了这句，后面 {line.Frees} 句台词也会跟着自动确定。", FontSize = 11, Foreground = ThemeHelper.Brush(tint.Accent) });

            void CommitChoice(UserChoice choice, string label)
            {
                try
                {
                    library.Core.DramaSetSpeaker(bookId, line.Section, line.Sentence, line.Start, choice);
                    lastDecision = (line.Section, line.Sentence, line.Start, label, line.Frees);
                    onChanged();
                    RebuildCard();
                }
                catch { }
            }

            var candRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var names = line.Names ?? [];
            for (int i = 0; i < Math.Min(3, names.Length); i++)
            {
                string nm = names[i];
                var cb = new Button
                {
                    Style = (Style)Application.Current.Resources[i == 0 ? "YYPrimaryPillButtonStyle" : "YYPillButtonStyle"],
                    Content = $"{i + 1}. {nm}",
                };
                cb.Click += (_, _) => CommitChoice(new UserChoice.Person(nm), nm);
                candRow.Children.Add(cb);
            }
            host.Children.Add(candRow);

            var customNameBox = new TextBox { PlaceholderText = "或是其他角色名字（输入回车确认）…", FontSize = 12 };
            customNameBox.KeyDown += (_, e) =>
            {
                if (e.Key == Windows.System.VirtualKey.Enter && !string.IsNullOrWhiteSpace(customNameBox.Text))
                {
                    string custom = customNameBox.Text.Trim();
                    CommitChoice(new UserChoice.Person(custom), custom);
                }
            };
            host.Children.Add(customNameBox);

            var subActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var notSpeechBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "不是台词（旁白）", FontSize = 11.5 };
            notSpeechBtn.Click += (_, _) => CommitChoice(new UserChoice.NotSpeech(), "旁白");
            var groupBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "众人齐声", FontSize = 11.5 };
            groupBtn.Click += (_, _) => CommitChoice(new UserChoice.Group("众人"), "众人");
            var skipBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "跳过 →", FontSize = 11.5 };
            skipBtn.Click += (_, _) => { skipOffset++; RebuildCard(); };
            subActions.Children.Add(notSpeechBtn);
            subActions.Children.Add(groupBtn);
            subActions.Children.Add(skipBtn);
            host.Children.Add(subActions);
        }

        RebuildCard();
        dlgRef = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "确认台词说话人",
            Content = host,
            CloseButtonText = "完成",
        };
        await dlgRef.ShowAsync();
    }

    public static async Task ShowRoleDetailDialogAsync(
        Window ownerWindow, XamlRoot xamlRoot, LibraryService library, string bookId, string roleName,
        Func<string, Action<string>, Task> openVoicePicker, Action onChanged)
    {
        bool dark = ThemeHelper.IsDark(xamlRoot.Content is FrameworkElement fe ? fe.ActualTheme : ElementTheme.Light);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(library.Prefs.Tint, dark);

        var host = new StackPanel { Width = 390, Spacing = 12 };

        void RebuildRole()
        {
            host.Children.Clear();
            string[] aliases;
            CharacterGender gender;
            RoleVoice? curRoleVoice = null;
            try
            {
                aliases = library.Core.DramaNames(bookId, roleName);
                gender = library.Core.DramaGender(bookId, roleName);
                curRoleVoice = library.Core.BookRoles(bookId).FirstOrDefault(r => r.Name == roleName)?.Voice;
            }
            catch
            {
                aliases = [roleName];
                gender = CharacterGender.Unknown;
            }
            if (aliases.Length == 0) aliases = [roleName];

            // Gender picker (男 / 女 / 不知道)
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
                    try { library.Core.DramaEditCast(bookId, new CastEdit.SetGender(roleName, gv)); } catch { }
                    onChanged();
                    RebuildRole();
                };
                Grid.SetColumn(gb, i);
                genderRow.Children.Add(gb);
            }
            host.Children.Add(new TextBlock { Text = "角色性别（用于自动匹配声线）", FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T3) });
            host.Children.Add(genderRow);

            // Aliases chips (RoleNamesSection)
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
                        try { library.Core.DramaEditCast(bookId, new CastEdit.Show(al)); onChanged(); RebuildRole(); } catch { }
                    }
                };
                chipSp.Children.Add(mainBtn);
                if (!primary)
                {
                    var splitBtn = new Button { Content = "×", Padding = new Thickness(3, 0, 3, 0), FontSize = 11, Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0) };
                    splitBtn.Click += (_, _) =>
                    {
                        try { library.Core.DramaEditCast(bookId, new CastEdit.Split(al)); onChanged(); RebuildRole(); } catch { }
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
                    try { library.Core.DramaEditCast(bookId, new CastEdit.AddName(roleName, addAliasBox.Text.Trim())); onChanged(); RebuildRole(); } catch { }
                }
            };
            host.Children.Add(addAliasBox);

            // Pitch & Rate sliders + Voice buttons
            double curRate = curRoleVoice?.Rate ?? 1.0;
            double curPitch = curRoleVoice?.Pitch ?? 0.0;
            string? curVid = curRoleVoice?.Voice;

            var voiceActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var pickVBtn = new Button { Style = (Style)Application.Current.Resources["YYPrimaryPillButtonStyle"], Content = "选用专属音色…" };
            pickVBtn.Click += async (_, _) =>
            {
                await openVoicePicker($"{roleName}的声音", vid =>
                {
                    try { library.Core.SetBookRole(bookId, roleName, new RoleVoice(vid, curRate, curPitch, false)); } catch { }
                    onChanged();
                });
            };
            var cloneVBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "🎙 为他录一段声音…" };
            cloneVBtn.Click += async (_, _) =>
            {
                await ShowCloneVoiceDialogAsync(ownerWindow, xamlRoot, library, bookId, roleName);
                onChanged();
            };
            voiceActions.Children.Add(pickVBtn);
            voiceActions.Children.Add(cloneVBtn);
            host.Children.Add(voiceActions);

            var pitchSlider = new Slider { Header = $"音高微调（{curPitch:+0.0;-0.0;0.0} 半音）", Minimum = -4.0, Maximum = 4.0, StepFrequency = 0.5, Value = curPitch };
            pitchSlider.ValueChanged += (_, e) =>
            {
                try { library.Core.SetBookRole(bookId, roleName, new RoleVoice(curVid, curRate, e.NewValue, false)); } catch { }
            };
            host.Children.Add(pitchSlider);
        }

        RebuildRole();
        await new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = $"人物设定 · {roleName}",
            Content = host,
            CloseButtonText = "完成",
        }.ShowAsync();
    }
}
