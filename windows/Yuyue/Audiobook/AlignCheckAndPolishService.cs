using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.System;
using Windows.UI;
using Yuyue.Core;
using Yuyue.Design;
using Yuyue.Speech;
using TimeSpan = System.TimeSpan;

namespace Yuyue.Audiobook;

/// <summary>
/// 1:1 port of <c>apple/Yuyue/Library/AlignCheck.swift</c>, <c>Speech/TextPolish.swift</c> &amp; <c>App/RecordingShare.swift</c>:
/// - <see cref="OpenAlignCheckWindow"/>: dedicated 1080×700 Alignment Check &amp; Fine-Tuning window (chapter prose with amber unsure stretches,
///   2-second ruler timeline + sentence blocks + waveform, Space preview, Left/Right ±0.1s nudge via <c>Core.AudiobookPin</c>).
/// - <see cref="ShowTextPolishDialogAsync"/>: LLM homophone/name cleanup for transcribed text versions (<c>Core.AudiobookPolishAsks</c> / <c>AudiobookPolishApply</c>).
/// - <see cref="RecordingShareServer"/>: LAN HTTP server authenticated via <c>x-yuyue-token</c> (<c>Core.LanToken</c>) serving <c>/has</c>, <c>/piece</c>, <c>/drama</c>, <c>/card</c>.
/// </summary>
public static class AlignCheckAndPolishService
{
    public static void OpenAlignCheckWindow(LibraryService library, string audioBookId, int initialRowIndex = 0, Action? onSaved = null)
    {
        var win = new Window { Title = "对齐检查" };
        ThemeHelper.ResizeWindow(win, 1080, 700, minWidthDip: 820, minHeightDip: 540);

        bool dark = ThemeHelper.IsDark(library.Prefs.ToElementTheme());
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(library.Prefs.Tint, dark);

        AudioRecording? recording = null;
        SyncMap? map = null;
        try
        {
            recording = library.Core.AudioRecording(audioBookId);
            map = library.Core.AudiobookMap(audioBookId);
        }
        catch { }

        var root = new Grid
        {
            RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light,
            Background = ThemeHelper.Brush(p.Bg),
        };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(54) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var topBar = new Grid { Padding = new Thickness(0, 0, 148, 0) };
        topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dragBar = new Grid { Background = new SolidColorBrush(Colors.Transparent), Padding = new Thickness(20, 0, 12, 0) };
        topBar.Children.Add(dragBar);
        root.Children.Add(topBar);
        win.Content = root;
        win.SetTitleBar(dragBar);
        ThemeHelper.ConfigureTitleBar(win, dark);

        if (recording is null || map is null || map.Rows.Length == 0)
        {
            var empty = new StackPanel
            {
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            empty.Children.Add(YYIcons.Make("wave", 28, ThemeHelper.Brush(p.T3)));
            empty.Children.Add(new TextBlock
            {
                Text = "尚未生成句级对齐数据",
                FontSize = 14,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = ThemeHelper.Brush(p.T1),
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            empty.Children.Add(new TextBlock
            {
                Text = "先将有声书与电子书配对，即可在此微调每一句的起止时刻。",
                FontSize = 12,
                Foreground = ThemeHelper.Brush(p.T3),
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            Grid.SetRow(empty, 1);
            root.Children.Add(empty);
            win.Activate();
            return;
        }

        string ebookId = map.BookId;
        string bookTitle = library.GetBooks().FirstOrDefault(b => b.Id == ebookId)?.Title
            ?? library.GetBooks().FirstOrDefault(b => b.Id == audioBookId)?.Title
            ?? "有声书";
        win.Title = $"{bookTitle} · 对齐检查";

        var sectionTexts = new Dictionary<uint, string[]>();
        string[] GetSectionSentences(uint sec)
        {
            if (sectionTexts.TryGetValue(sec, out var cached)) return cached;
            try
            {
                var arr = library.Core.Sentences(ebookId, sec);
                sectionTexts[sec] = arr;
                return arr;
            }
            catch
            {
                sectionTexts[sec] = [];
                return [];
            }
        }

        // Group unsure stretches (Conf < 140)
        List<(int StartIdx, int EndIdx)> ComputeUnsureStretches(SyncRow[] rows)
        {
            var list = new List<(int, int)>();
            int i = 0;
            while (i < rows.Length)
            {
                if (rows[i].Conf < 140)
                {
                    int start = i;
                    while (i + 1 < rows.Length && rows[i + 1].Section == rows[start].Section && rows[i + 1].Conf < 140)
                        i++;
                    list.Add((start, i));
                }
                i++;
            }
            return list;
        }

        var stops = ComputeUnsureStretches(map.Rows);
        int at = Math.Clamp(initialRowIndex, 0, map.Rows.Length - 1);
        if (initialRowIndex == 0 && stops.Count > 0)
            at = stops[0].StartIdx;
        int? activeStop = stops.FindIndex(s => s.StartIdx <= at && at <= s.EndIdx) is >= 0 and int si ? si : null;
        var pinnedIndices = new HashSet<int>();

        bool closed = false;
        int waveSampleGen = 0;
        var previewPlayer = new MediaPlayer { AutoPlay = false };
        var stopPreviewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        var windowCts = new CancellationTokenSource();
        var decodingTracks = new ConcurrentDictionary<int, byte>();
        string pcmScratchDir = Path.Combine(library.DataDir, "audiobooks", audioBookId, "pcm-cache");
        double previewEndInTrackSec = double.MaxValue;
        stopPreviewTimer.Tick += (_, _) =>
        {
            if (closed) return;
            try
            {
                if (previewPlayer.PlaybackSession.Position.TotalSeconds >= previewEndInTrackSec)
                {
                    previewPlayer.Pause();
                    stopPreviewTimer.Stop();
                }
            }
            catch { }
        };
        win.Closed += (_, _) =>
        {
            closed = true;
            try
            {
                windowCts.Cancel();
                if (decodingTracks.IsEmpty)
                    windowCts.Dispose();
            }
            catch { }
            stopPreviewTimer.Stop();
            try
            {
                previewPlayer.Pause();
                previewPlayer.Source = null;
                previewPlayer.Dispose();
            }
            catch { }
        };

        string ResolveTrackPath(string rawPath) =>
            Path.IsPathRooted(rawPath) ? rawPath : Path.Combine(library.DataDir, rawPath);

        void PlayAroundRow(SyncRow row)
        {
            if (closed) return;
            double globalStart = Math.Max(0, row.T0Ms / 1000.0 - 0.25);
            double globalEnd = Math.Max(globalStart + 1.2, row.T1Ms / 1000.0 + 0.35);

            double accum = 0;
            for (int fi = 0; fi < recording.Files.Length; fi++)
            {
                var f = recording.Files[fi];
                double next = accum + Math.Max(0.1, f.Seconds);
                if (globalStart < next || fi == recording.Files.Length - 1)
                {
                    string trackPath = ResolveTrackPath(f.Path);
                    if (!File.Exists(trackPath)) return;
                    double inTrackStart = Math.Max(0, globalStart - accum);
                    previewEndInTrackSec = Math.Min(f.Seconds, globalEnd - accum);
                    App.CurrentApp.ClaimAudioPlayback(win, () => win.DispatcherQueue.TryEnqueue(() =>
                    {
                        if (closed) return;
                        try { previewPlayer.Pause(); stopPreviewTimer.Stop(); } catch { }
                    }));
                    previewPlayer.Source = MediaSource.CreateFromUri(new Uri(trackPath));
                    previewPlayer.PlaybackSession.Position = TimeSpan.FromSeconds(inTrackStart);
                    previewPlayer.Play();
                    stopPreviewTimer.Start();
                    return;
                }
                accum = next;
            }
        }

        // Top Toolbar (populate dragBar in Col 0 and rightTools in Col 1)
        var titleStack = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        var mainTitleTb = new TextBlock { Text = $"{bookTitle} · {L10n.T("对齐检查")}", FontSize = 13.5, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = ThemeHelper.Brush(p.T1) };
        var subTitleTb = new TextBlock { FontSize = 11, Foreground = ThemeHelper.Brush(p.T3) };
        titleStack.Children.Add(mainTitleTb);
        titleStack.Children.Add(subTitleTb);
        dragBar.Children.Add(titleStack);

        var rightTools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        var stopCounterTb = new TextBlock { FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T2), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
        var prevStopBtn = new Button { Style = (Style)Application.Current.Resources["YYToolbarIconButtonStyle"], Content = YYIcons.Make("chevron-left", 11.5, ThemeHelper.Brush(p.T1)) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(prevStopBtn, L10n.T("上一处待确认"));
        var nextStopBtn = new Button { Style = (Style)Application.Current.Resources["YYToolbarIconButtonStyle"], Content = YYIcons.Make("chevron-right", 11.5, ThemeHelper.Brush(p.T1)) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(nextStopBtn, L10n.T("下一处待确认"));
        var doneBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentSoftAccentButtonStyle"], Height = 28, MinHeight = 28, Padding = new Thickness(14, 0, 14, 0), Content = L10n.T("完成") };
        doneBtn.Click += (_, _) => { onSaved?.Invoke(); win.Close(); };

        rightTools.Children.Add(stopCounterTb);
        rightTools.Children.Add(prevStopBtn);
        rightTools.Children.Add(nextStopBtn);
        rightTools.Children.Add(doneBtn);
        Grid.SetColumn(rightTools, 1);
        topBar.Children.Add(rightTools);

        // Body: Left Prose + Bottom Timeline | Right 288px Inspector
        var body = new Grid { Padding = new Thickness(0, 0, 14, 14) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(288) });
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        var leftArea = new Grid();
        leftArea.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        leftArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.Children.Add(leftArea);

        var proseHost = new StackPanel { Width = 500, Spacing = 10, Padding = new Thickness(0, 20, 0, 32), HorizontalAlignment = HorizontalAlignment.Center };
        var proseScroll = new ScrollViewer { Content = proseHost, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        leftArea.Children.Add(proseScroll);

        var timelineHost = new Border
        {
            Height = 112,
            Margin = new Thickness(18, 4, 18, 0),
            CornerRadius = new CornerRadius(18),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16, 10, 16, 10),
        };
        Grid.SetRow(timelineHost, 1);
        leftArea.Children.Add(timelineHost);

        var inspectorStack = new StackPanel { Spacing = 12 };
        var inspectorCard = new Border
        {
            CornerRadius = new CornerRadius(16),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16),
            VerticalAlignment = VerticalAlignment.Top,
            Child = inspectorStack,
        };
        Grid.SetColumn(inspectorCard, 1);
        body.Children.Add(inspectorCard);

        static string FormatClock(double sec, bool tenths = true)
        {
            sec = Math.Max(0, sec);
            int mins = (int)(sec / 60);
            double rem = sec - mins * 60;
            return tenths ? $"{mins}:{rem:00.0}" : $"{mins}:{(int)rem:00}";
        }

        void RenderAll()
        {
            if (closed || map is null || map.Rows.Length == 0) return;
            at = Math.Clamp(at, 0, map.Rows.Length - 1);
            var cur = map.Rows[at];
            activeStop = stops.FindIndex(s => s.StartIdx <= at && at <= s.EndIdx) is >= 0 and int idx ? idx : null;

            int unfixedStops = stops.Count(st => !Enumerable.Range(st.StartIdx, st.EndIdx - st.StartIdx + 1).Any(pinnedIndices.Contains));
            subTitleTb.Text = $"第 {cur.Section + 1} 章 · 第 {cur.Sentence + 1} 句{(unfixedStops > 0 ? $" · {unfixedStops} 处建议核对" : " · 已全部核对")}";
            stopCounterTb.Text = stops.Count == 0
                ? "没有要看的地方"
                : $"第 {(activeStop is int a ? (a + 1).ToString() : "–")} / {stops.Count} 处";
            prevStopBtn.IsEnabled = stops.Count > 0 && (activeStop ?? stops.Count) > 0;
            nextStopBtn.IsEnabled = stops.Count > 0 && (activeStop ?? -1) + 1 < stops.Count;

            // 1. Render Prose
            proseHost.Children.Clear();
            var sents = GetSectionSentences(cur.Section);
            proseHost.Children.Add(new TextBlock
            {
                Text = $"第 {cur.Section + 1} 章",
                FontSize = 19,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = ThemeHelper.Brush(p.T1),
                Margin = new Thickness(0, 0, 0, 6),
            });

            int centerSent = (int)cur.Sentence;
            int fromS = Math.Max(0, centerSent - 8);
            int toS = Math.Min(sents.Length - 1, centerSent + 10);

            (int StartIdx, int EndIdx)? curStretch = activeStop is int sIdx ? stops[sIdx] : null;
            int stretchMinSent = curStretch is { } cs ? (int)map.Rows[cs.StartIdx].Sentence : -1;
            int stretchMaxSent = curStretch is { } ce ? (int)map.Rows[ce.EndIdx].Sentence : -1;

            for (int s = fromS; s <= toS; s++)
            {
                int rowIdx = Array.FindIndex(map.Rows, r => r.Section == cur.Section && r.Sentence == (uint)s);
                bool isCurrent = rowIdx == at;
                bool inUnsure = s >= stretchMinSent && s <= stretchMaxSent && stretchMinSent >= 0;
                bool wasPinned = rowIdx >= 0 && pinnedIndices.Contains(rowIdx);

                Color borderCol = isCurrent ? tint.Accent : (inUnsure ? (wasPinned ? p.Ok : Color.FromArgb(255, 217, 148, 38)) : Colors.Transparent);
                Color bgCol = isCurrent ? tint.Soft : (s % 2 == 1 ? p.Inset : Colors.Transparent);

                var rowSp = new StackPanel { Spacing = 3 };
                if (inUnsure && s == stretchMinSent)
                {
                    rowSp.Children.Add(new TextBlock
                    {
                        Text = wasPinned ? "✓ 调过了" : "⚠ 这一段对得不太准",
                        FontSize = 10.5,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        Foreground = ThemeHelper.Brush(wasPinned ? p.Ok : Color.FromArgb(255, 217, 148, 38)),
                    });
                }
                rowSp.Children.Add(new TextBlock
                {
                    Text = s < sents.Length ? sents[s] : $"（第 {s + 1} 句）",
                    FontFamily = new FontFamily(YYFontFamily.Serif.WindowsFamily()),
                    FontSize = 15,
                    FontWeight = isCurrent ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                    LineHeight = 25,
                    Foreground = ThemeHelper.Brush(isCurrent ? tint.Accent : p.T1),
                    TextWrapping = TextWrapping.Wrap,
                });

                var sentBtn = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(12, 8, 12, 8),
                    CornerRadius = new CornerRadius(10),
                    Background = ThemeHelper.Brush(bgCol),
                    BorderBrush = ThemeHelper.Brush(borderCol),
                    BorderThickness = new Thickness(isCurrent || inUnsure ? 1.5 : 0),
                    UseSystemFocusVisuals = false,
                    Content = rowSp,
                };
                if (rowIdx >= 0)
                {
                    int targetRow = rowIdx;
                    sentBtn.Click += (_, _) => { at = targetRow; RenderAll(); };
                }
                proseHost.Children.Add(sentBtn);
            }

            // 2. Render Timeline (11-second window centered on cur.T0Ms)
            double centerSec = cur.T0Ms / 1000.0;
            double winLo = Math.Max(0, centerSec - 4.5);
            double winHi = winLo + 11.0;

            var tlStack = new StackPanel { Spacing = 6 };
            var rulerGrid = new Grid();
            for (int tick = 0; tick <= 5; tick++)
            {
                rulerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                double tSec = winLo + tick * 2.0;
                var tTb = new TextBlock { Text = FormatClock(tSec, tenths: false), FontSize = 10, Foreground = ThemeHelper.Brush(p.T3) };
                Grid.SetColumn(tTb, tick);
                rulerGrid.Children.Add(tTb);
            }
            tlStack.Children.Add(rulerGrid);

            var blocksRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            foreach (var rIdx in Enumerable.Range(Math.Max(0, at - 2), Math.Min(6, map.Rows.Length - Math.Max(0, at - 2))))
            {
                var r = map.Rows[rIdx];
                bool sel = rIdx == at;
                var secArr = GetSectionSentences(r.Section);
                string txt = r.Sentence < secArr.Length ? secArr[r.Sentence] : $"句 {r.Sentence + 1}";
                double dur = Math.Max(0.8, (r.T1Ms - r.T0Ms) / 1000.0);
                double blockW = Math.Clamp(dur * 38, 64, 180);

                var blkBtn = new Button
                {
                    Width = blockW,
                    Height = 28,
                    Padding = new Thickness(8, 0, 8, 0),
                    CornerRadius = new CornerRadius(7),
                    Background = ThemeHelper.Brush(sel ? tint.Soft : p.Inset),
                    BorderBrush = ThemeHelper.Brush(sel ? tint.Accent : p.Line),
                    BorderThickness = new Thickness(sel ? 1.5 : 1),
                    UseSystemFocusVisuals = false,
                    Content = new TextBlock
                    {
                        Text = txt,
                        FontFamily = new FontFamily(YYFontFamily.Serif.WindowsFamily()),
                        FontSize = 11,
                        Foreground = ThemeHelper.Brush(sel ? tint.Accent : p.T1),
                        MaxLines = 1,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    },
                };
                int pickIdx = rIdx;
                blkBtn.Click += (_, _) => { at = pickIdx; RenderAll(); };
                blocksRow.Children.Add(blkBtn);
            }
            tlStack.Children.Add(blocksRow);

            // Real PCM amplitude peak waveform bars around [winLo, winHi] (offloaded off UI thread!)
            const int barCount = 84;
            var barBorders = new Border[barCount];
            var wavePanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Height = 26, VerticalAlignment = VerticalAlignment.Center };
            double curT0Sec = cur.T0Ms / 1000.0;
            double curT1Sec = cur.T1Ms / 1000.0;
            for (int b = 0; b < barCount; b++)
            {
                double t = winLo + (winHi - winLo) * (b + 0.5) / barCount;
                bool insideCur = t >= curT0Sec && t <= curT1Sec;
                var barEl = new Border
                {
                    Width = 3,
                    Height = insideCur ? 6.0 : 3.5,
                    CornerRadius = new CornerRadius(1.5),
                    VerticalAlignment = VerticalAlignment.Center,
                    Background = ThemeHelper.Brush(insideCur ? tint.Accent : p.T3),
                    Opacity = insideCur ? 0.9 : 0.45,
                };
                barBorders[b] = barEl;
                wavePanel.Children.Add(barEl);
            }
            tlStack.Children.Add(wavePanel);
            timelineHost.Child = tlStack;

            int myWaveGen = ++waveSampleGen;
            var filesSnap = recording.Files;
            _ = Task.Run(() =>
            {
                if (closed) return;
                var peaks = new float[barCount];
                bool hasRealPeaks = false;
                double trackAccum = 0;
                for (int fi = 0; fi < filesSnap.Length; fi++)
                {
                    if (closed) return;
                    var f = filesSnap[fi];
                    double dur = Math.Max(0.05, f.Seconds);
                    double trackLo = trackAccum;
                    double trackHi = trackAccum + dur;
                    trackAccum = trackHi;

                    if (trackHi <= winLo || trackLo >= winHi)
                        continue;

                    double ovLo = Math.Max(winLo, trackLo);
                    double ovHi = Math.Min(winHi, trackHi);
                    string srcPath = ResolveTrackPath(f.Path);
                    string cachedWav1 = Path.Combine(pcmScratchDir, $"pcm16k-{fi}.wav");
                    string cachedWav2 = Path.Combine(library.DataDir, "audiobook-work", audioBookId, $"pcm16k-{fi}.wav");

                    string? readableWav = null;
                    if (File.Exists(cachedWav1)) readableWav = cachedWav1;
                    else if (File.Exists(cachedWav2)) readableWav = cachedWav2;
                    else if (srcPath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) && File.Exists(srcPath)) readableWav = srcPath;

                    if (readableWav is not null &&
                        TrySampleWavSpanPeaks(readableWav, ovLo - trackLo, ovHi - trackLo, trackLo, winLo, winHi, peaks))
                    {
                        hasRealPeaks = true;
                    }
                    else if (!closed && File.Exists(srcPath) && decodingTracks.TryAdd(fi, 1))
                    {
                        int idxCap = fi;
                        string srcCap = srcPath;
                        CancellationToken ct;
                        try { ct = windowCts.Token; } catch { decodingTracks.TryRemove(idxCap, out _); continue; }
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await Aligner.EnsurePcm16kWavAsync(srcCap, pcmScratchDir, idxCap, ct);
                                if (!closed && !ct.IsCancellationRequested)
                                {
                                    win.DispatcherQueue.TryEnqueue(() =>
                                    {
                                        if (!closed) RenderAll();
                                    });
                                }
                            }
                            catch { }
                            finally
                            {
                                decodingTracks.TryRemove(idxCap, out _);
                                if (closed && decodingTracks.IsEmpty)
                                {
                                    try { windowCts.Dispose(); } catch { }
                                }
                            }
                        });
                    }
                }

                if (!hasRealPeaks || closed || myWaveGen != waveSampleGen) return;
                float maxPeak = 0f;
                for (int b = 0; b < barCount; b++)
                    if (peaks[b] > maxPeak) maxPeak = peaks[b];
                float normPeak = Math.Max(0.08f, maxPeak);

                win.DispatcherQueue.TryEnqueue(() =>
                {
                    if (closed || myWaveGen != waveSampleGen) return;
                    for (int b = 0; b < barCount; b++)
                    {
                        barBorders[b].Height = Math.Clamp(3.0 + Math.Pow(Math.Clamp(peaks[b] / normPeak, 0f, 1f), 0.72) * 21.0, 3.0, 24.0);
                    }
                });
            });

            // 3. Render Right Inspector
            inspectorStack.Children.Clear();
            inspectorStack.Children.Add(new TextBlock
            {
                Text = "微调这一句",
                FontSize = 13.5,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = ThemeHelper.Brush(p.T1),
            });

            var curSents = GetSectionSentences(cur.Section);
            string curText = cur.Sentence < curSents.Length ? curSents[cur.Sentence] : "";
            inspectorStack.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(10),
                Background = ThemeHelper.Brush(p.Inset),
                Padding = new Thickness(12, 10, 12, 10),
                Child = new TextBlock
                {
                    Text = curText,
                    FontFamily = new FontFamily(YYFontFamily.Serif.WindowsFamily()),
                    FontSize = 13,
                    LineHeight = 21,
                    Foreground = ThemeHelper.Brush(p.T1),
                    MaxLines = 4,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextWrapping = TextWrapping.Wrap,
                },
            });

            var playBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYFluentSoftAccentButtonStyle"],
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Height = 34,
                Content = "▶ 试听这一句（空格）",
            };
            playBtn.Click += (_, _) => PlayAroundRow(cur);
            inspectorStack.Children.Add(playBtn);

            inspectorStack.Children.Add(new TextBlock
            {
                Text = $"起止时刻：{FormatClock(cur.T0Ms / 1000.0)} – {FormatClock(cur.T1Ms / 1000.0)}",
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = ThemeHelper.Brush(p.T1),
                HorizontalAlignment = HorizontalAlignment.Center,
            });

            void NudgeStart(double deltaSec)
            {
                double nextSec = Math.Max(0, cur.T0Ms / 1000.0 + deltaSec);
                try
                {
                    library.Core.AudiobookPin(audioBookId, cur.Section, cur.Sentence, nextSec);
                    pinnedIndices.Add(at);
                    map = library.Core.AudiobookMap(audioBookId) ?? map;
                    RenderAll();
                    PlayAroundRow(map.Rows[Math.Clamp(at, 0, map.Rows.Length - 1)]);
                }
                catch { }
            }

            var nudgeRow = new Grid { ColumnSpacing = 4 };
            (string Lbl, double Delta)[] nudges = [("−0.5s", -0.5), ("−0.1s", -0.1), ("+0.1s", 0.1), ("+0.5s", 0.5)];
            for (int ni = 0; ni < nudges.Length; ni++)
            {
                nudgeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var (lbl, d) = nudges[ni];
                var nb = new Button
                {
                    Style = (Style)Application.Current.Resources["YYFluentButtonStyle"],
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Padding = new Thickness(0),
                    Height = 30,
                    FontSize = 11,
                    Content = lbl,
                };
                double delta = d;
                nb.Click += (_, _) => NudgeStart(delta);
                Grid.SetColumn(nb, ni);
                nudgeRow.Children.Add(nb);
            }
            inspectorStack.Children.Add(nudgeRow);

            var navSentRow = new Grid { ColumnSpacing = 8 };
            navSentRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            navSentRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var upBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentButtonStyle"], HorizontalAlignment = HorizontalAlignment.Stretch, Content = "↑ 上一句", IsEnabled = at > 0 };
            upBtn.Click += (_, _) => { at = Math.Max(0, at - 1); RenderAll(); };
            var downBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentButtonStyle"], HorizontalAlignment = HorizontalAlignment.Stretch, Content = "↓ 下一句", IsEnabled = at + 1 < map.Rows.Length };
            downBtn.Click += (_, _) => { at = Math.Min(map.Rows.Length - 1, at + 1); RenderAll(); };
            Grid.SetColumn(downBtn, 1);
            navSentRow.Children.Add(upBtn);
            navSentRow.Children.Add(downBtn);
            inspectorStack.Children.Add(navSentRow);

            inspectorStack.Children.Add(new TextBlock
            {
                Text = "快捷键：空格 试听 · ← / → 微调 ±0.1 秒 · ↑ / ↓ 切换句子。定准一句后，前后几句会自动跟着重排。",
                FontSize = 11,
                Foreground = ThemeHelper.Brush(p.T3),
                TextWrapping = TextWrapping.Wrap,
            });
        }

        prevStopBtn.Click += (_, _) =>
        {
            int target = (activeStop ?? stops.Count) - 1;
            if (target >= 0 && target < stops.Count) { at = stops[target].StartIdx; RenderAll(); }
        };
        nextStopBtn.Click += (_, _) =>
        {
            int target = (activeStop ?? -1) + 1;
            if (target >= 0 && target < stops.Count) { at = stops[target].StartIdx; RenderAll(); }
        };

        root.PreviewKeyDown += (_, e) =>
        {
            if (closed || YYChrome.HasBlockingModalOrFlyoutOpen(root.XamlRoot)) return;
            bool ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            bool alt = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            if (ctrl && !alt)
            {
                if (e.Key == VirtualKey.W)
                {
                    e.Handled = true;
                    win.Close();
                    return;
                }
                if (e.Key == (VirtualKey)188)
                {
                    e.Handled = true;
                    App.CurrentApp.OpenSettings();
                    return;
                }
            }
            if (map is null || map.Rows.Length == 0) return;
            var cur = map.Rows[Math.Clamp(at, 0, map.Rows.Length - 1)];
            switch (e.Key)
            {
                case VirtualKey.Space:
                    PlayAroundRow(cur);
                    e.Handled = true;
                    break;
                case VirtualKey.Left:
                    try
                    {
                        library.Core.AudiobookPin(audioBookId, cur.Section, cur.Sentence, Math.Max(0, cur.T0Ms / 1000.0 - 0.1));
                        pinnedIndices.Add(at);
                        map = library.Core.AudiobookMap(audioBookId) ?? map;
                        RenderAll();
                    }
                    catch { }
                    e.Handled = true;
                    break;
                case VirtualKey.Right:
                    try
                    {
                        library.Core.AudiobookPin(audioBookId, cur.Section, cur.Sentence, cur.T0Ms / 1000.0 + 0.1);
                        pinnedIndices.Add(at);
                        map = library.Core.AudiobookMap(audioBookId) ?? map;
                        RenderAll();
                    }
                    catch { }
                    e.Handled = true;
                    break;
                case VirtualKey.Up:
                    at = Math.Max(0, at - 1);
                    RenderAll();
                    e.Handled = true;
                    break;
                case VirtualKey.Down:
                    at = Math.Min(map.Rows.Length - 1, at + 1);
                    RenderAll();
                    e.Handled = true;
                    break;
            }
        };

        RenderAll();
        win.Activate();
    }

    private static bool TrySampleWavSpanPeaks(
        string wavPath,
        double localLoSec,
        double localHiSec,
        double trackGlobalLoSec,
        double winLoSec,
        double winHiSec,
        float[] bins)
    {
        if (bins.Length == 0 || winHiSec <= winLoSec) return false;
        if (!Aligner.TryReadWavInfo(wavPath, out var info)) return false;
        if (info.SampleRate <= 0 || info.Channels <= 0 || info.DataOffset <= 0 || info.DataBytes <= 0)
            return false;

        bool validPcm = info.AudioFormat == 1 && info.BitsPerSample is 8 or 16 or 24 or 32;
        bool validFloat = info.AudioFormat == 3 && info.BitsPerSample == 32;
        if (!validPcm && !validFloat) return false;

        int bytesPerSample = info.BitsPerSample / 8;
        int frameBytes = info.Channels * bytesPerSample;
        if (frameBytes <= 0) return false;

        long totalFrames = info.DataBytes / frameBytes;
        if (totalFrames <= 0) return false;

        long startFrame = Math.Clamp((long)Math.Floor(Math.Max(0, localLoSec) * info.SampleRate), 0, totalFrames);
        long endFrame = Math.Clamp((long)Math.Ceiling(Math.Max(localLoSec, localHiSec) * info.SampleRate), startFrame, totalFrames);
        long spanFrames = endFrame - startFrame;
        if (spanFrames <= 0) return false;

        try
        {
            using var fs = new FileStream(wavPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long byteStart = info.DataOffset + startFrame * frameBytes;
            if (byteStart >= fs.Length) return false;
            fs.Seek(byteStart, SeekOrigin.Begin);

            int stride = Math.Max(1, (int)(spanFrames / (bins.Length * 160L)));
            int chunkFrames = Math.Max(256, 65536 / frameBytes);
            byte[] buf = new byte[chunkFrames * frameBytes];
            long frameCursor = startFrame;
            double invRate = 1.0 / info.SampleRate;
            double winSpan = winHiSec - winLoSec;
            bool sampledAny = false;

            while (frameCursor < endFrame)
            {
                int wantFrames = (int)Math.Min(chunkFrames, endFrame - frameCursor);
                int readBytes = fs.Read(buf, 0, wantFrames * frameBytes);
                if (readBytes < frameBytes) break;
                int gotFrames = readBytes / frameBytes;

                for (int i = 0; i < gotFrames; i += stride)
                {
                    double globalSec = trackGlobalLoSec + (frameCursor + i) * invRate;
                    int bin = (int)((globalSec - winLoSec) / winSpan * bins.Length);
                    if ((uint)bin >= (uint)bins.Length) continue;

                    int baseOff = i * frameBytes;
                    float framePeak = 0f;
                    for (int ch = 0; ch < info.Channels; ch++)
                    {
                        int sOff = baseOff + ch * bytesPerSample;
                        float amp = info.AudioFormat == 3
                            ? Math.Abs(BitConverter.ToSingle(buf, sOff))
                            : info.BitsPerSample switch
                            {
                                8 => Math.Abs((buf[sOff] - 128) / 128f),
                                16 => Math.Abs(BinaryPrimitives.ReadInt16LittleEndian(buf.AsSpan(sOff, 2)) / 32768f),
                                24 => Math.Abs(((buf[sOff] | (buf[sOff + 1] << 8) | ((sbyte)buf[sOff + 2] << 16))) / 8388608f),
                                32 => Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(sOff, 4)) / 2147483648f),
                                _ => 0f,
                            };
                        if (float.IsFinite(amp) && amp > framePeak)
                            framePeak = amp;
                    }

                    if (framePeak > bins[bin])
                        bins[bin] = Math.Min(1f, framePeak);
                    sampledAny = true;
                }

                frameCursor += gotFrames;
            }

            return sampledAny;
        }
        catch
        {
            return false;
        }
    }

    private sealed class PolishJobState
    {
        public bool Running;
        public int Done;
        public int Total;
        public int Fixed;
        public int Failed;
        public string? Error;
        public CancellationTokenSource? Cts;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, PolishJobState> PolishJobs = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> PolishLastFixed = new();
    public static event Action<string>? PolishStateChanged;

    public static (bool Running, int Done, int Total, int Fixed, string? Error) GetPolishState(string bookId)
    {
        if (PolishJobs.TryGetValue(bookId, out var st))
            return (st.Running, st.Done, st.Total, st.Fixed, st.Error);
        return (false, 0, 0, PolishLastFixed.TryGetValue(bookId, out var f) ? f : 0, null);
    }

    public static void StopTextPolish(string bookId)
    {
        if (PolishJobs.TryGetValue(bookId, out var st))
            try { st.Cts?.Cancel(); } catch { }
    }

    /// <summary>
    /// 1:1 <c>TextPolish.swift</c>: polishes homophones and character names in a transcribed book (<c>文字版</c>)
    /// in the background per book (closing the sheet does NOT cancel an active job), stopping immediately on fatal
    /// auth/credit/budget errors and keeping only phonetic-preserving replacements so timestamps stay aligned.
    /// </summary>
    public static async Task ShowTextPolishDialogAsync(
        XamlRoot xamlRoot, LibraryService library, string bookId, Action? onFinished = null)
    {
        if (!library.Companion.IsConfigured)
        {
            App.CurrentApp.OpenSettings("companion");
            return;
        }

        PolishAsk[] asks;
        try { asks = library.Core.AudiobookPolishAsks(bookId); }
        catch { asks = []; }

        bool dark = ThemeHelper.IsDark(library.Prefs.ToElementTheme());
        var p = ThemeHelper.Palette(dark);

        if (asks.Length == 0)
        {
            await YYChrome.ShowNoticeAsync(
                xamlRoot,
                dark,
                L10n.T("整理错字"),
                L10n.T("这本书不是录音转写的「文字版」，或当前没有可整理的段落。"),
                okText: L10n.T("好"),
                tint: library.Prefs.Tint);
            return;
        }

        var job = PolishJobs.GetOrAdd(bookId, _ => new PolishJobState { Total = asks.Length });
        ContentDialog? dlgRef = null;
        var statusTb = new TextBlock
        {
            Text = $"共 {asks.Length} 段（每段约 2,000 字），仅修正同音错字与人名，不会破坏语音对齐。关闭窗口后仍可在后台继续。",
            FontSize = 12,
            LineHeight = 19,
            Foreground = ThemeHelper.Brush(p.T2),
            TextWrapping = TextWrapping.Wrap,
        };
        var bar = new ProgressBar { Minimum = 0, Maximum = Math.Max(1, asks.Length), Value = job.Done };
        var detailTb = new TextBlock { FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T3), TextWrapping = TextWrapping.Wrap };
        string startBtnLabelText = job.Running ? "停止后台整理" : "开始整理文字版";
        var startBtnLabel = new TextBlock
        {
            Text = L10n.T(startBtnLabelText),
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        };

        void RefreshUi()
        {
            bar.Maximum = Math.Max(1, job.Total > 0 ? job.Total : asks.Length);
            bar.Value = job.Done;
            if (job.Running)
            {
                startBtnLabel.Text = L10n.T("停止后台整理");
                detailTb.Text = $"正在后台整理第 {Math.Min(job.Done + 1, job.Total)} / {job.Total} 段 · 已修正 {job.Fixed} 处…";
            }
            else if (!string.IsNullOrEmpty(job.Error))
            {
                startBtnLabel.Text = L10n.T("重试整理文字版");
                detailTb.Text = $"整理中断：{job.Error}";
            }
            else if (job.Done > 0)
            {
                startBtnLabel.Text = L10n.T("再次整理文字版");
                statusTb.Text = job.Failed > 0
                    ? $"整理完成：共修正 {job.Fixed} 处同音错字（{job.Failed} 段请求未成功）。"
                    : $"✓ 整理完成：共修正 {job.Fixed} 处同音错字与人名。";
                detailTb.Text = $"已完成 {job.Done} / {job.Total} 段 · 共修正 {job.Fixed} 处";
            }
            else if (PolishLastFixed.TryGetValue(bookId, out var prevFixed))
            {
                detailTb.Text = $"上次整理共修正 {prevFixed} 处同音错字。";
            }
        }

        bool polishSheetClosed = false;
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        Action<string> onChanged = id =>
        {
            if (polishSheetClosed || id != bookId) return;
            dispatcher?.TryEnqueue(() =>
            {
                if (!polishSheetClosed)
                    RefreshUi();
            });
        };
        PolishStateChanged += onChanged;

        var startBtn = YYChrome.MakeDialogButton(startBtnLabel, () =>
        {
            if (job.Running)
            {
                StopTextPolish(bookId);
                return;
            }
            job.Running = true;
            job.Done = 0;
            job.Total = asks.Length;
            job.Fixed = 0;
            job.Failed = 0;
            job.Error = null;
            job.Cts = new CancellationTokenSource();
            var ct = job.Cts.Token;
            RefreshUi();

            _ = Task.Run(async () =>
            {
                try
                {
                    for (int i = 0; i < asks.Length; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        var ask = asks[i];
                        try
                        {
                            string reply = await library.Companion.ChatAsync(
                                ask.User,
                                ask.System,
                                maxTokens: 2048,
                                fast: true,
                                source: UsageSource.Polish,
                                ct: ct);
                            job.Fixed += (int)library.Core.AudiobookPolishApply(bookId, ask.Id, reply);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) when (IsFatalPolishError(ex))
                        {
                            job.Error = ex.Message;
                            break;
                        }
                        catch
                        {
                            job.Failed++;
                        }
                        job.Done = i + 1;
                        PolishStateChanged?.Invoke(bookId);
                    }
                    if (string.IsNullOrEmpty(job.Error))
                        PolishLastFixed[bookId] = job.Fixed;
                }
                catch (OperationCanceledException) { }
                finally
                {
                    job.Running = false;
                    job.Cts?.Dispose();
                    job.Cts = null;
                    PolishStateChanged?.Invoke(bookId);
                    dispatcher?.TryEnqueue(() => onFinished?.Invoke());
                }
            });
        }, dark, library.Prefs.Tint, "primary", height: 38, stretch: true);
        startBtnLabel.Foreground = startBtn.Foreground;

        RefreshUi();

        var cardBody = new StackPanel
        {
            Spacing = 10,
            Padding = new Thickness(14, 12, 14, 12),
            Children = { statusTb, bar, detailTb },
        };
        var sheetBody = new StackPanel
        {
            Spacing = 14,
            Padding = new Thickness(22, 18, 22, 18),
        };
        sheetBody.Children.Add(YYChrome.MakeDialogHeader(L10n.T("整理文字版错字"), null, () => dlgRef?.Hide(), dark, titleSize: 17));
        sheetBody.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0.75),
            Child = cardBody,
        });
        sheetBody.Children.Add(startBtn);

        dlgRef = YYChrome.CreateFluentDialog(xamlRoot, dark, 420, sheetBody, tint: library.Prefs.Tint);
        try
        {
            await dlgRef.ShowSafeAsync(library.Prefs.Tint);
        }
        finally
        {
            polishSheetClosed = true;
            PolishStateChanged -= onChanged;
        }
    }

    private static bool IsFatalPolishError(Exception ex)
    {
        string msg = ex.Message ?? "";
        return msg.Contains("401", StringComparison.Ordinal)
            || msg.Contains("402", StringComparison.Ordinal)
            || msg.Contains("403", StringComparison.Ordinal)
            || msg.Contains("密钥", StringComparison.Ordinal)
            || msg.Contains("余额", StringComparison.Ordinal)
            || msg.Contains("额度", StringComparison.Ordinal)
            || msg.Contains("上限", StringComparison.Ordinal);
    }
}

/// <summary>
/// 1:1 <c>RecordingServer</c> &amp; <c>RecordingFetch</c> in <c>apple/Yuyue/App/RecordingShare.swift</c>:
/// LAN HTTP server authenticated via <c>x-yuyue-token</c> (<c>Core.LanToken()</c>) with RFC 6762 mDNS
/// (<c>_yuyue-rec._tcp.local</c>) multicast announcement so mobile devices &amp; Macs automatically discover it
/// and pull audiobook recordings (<c>/has</c>, <c>/piece</c>), drama clips (<c>/drama</c>, <c>/clip</c>),
/// voice cards (<c>/card</c>), and trigger phone pre-render (<c>/want</c>).
/// </summary>
public sealed class RecordingShareServer : IDisposable
{
    public const string ServiceType = "_yuyue-rec._tcp.local";
    private const int PieceBytes = 8 * 1024 * 1024;
    private readonly LibraryService _library;
    private TcpListener? _listener;
    private UdpClient? _mdnsUdp;
    private CancellationTokenSource? _cts;

    public int Port { get; private set; }

    public RecordingShareServer(LibraryService library)
    {
        _library = library;
    }

    public void Start()
    {
        if (_listener is not null) return;
        try
        {
            string token = _library.Core.LanToken();
            if (string.IsNullOrEmpty(token)) return;

            _listener = new TcpListener(IPAddress.Any, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            _ = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        var client = await _listener.AcceptTcpClientAsync(ct);
                        _ = HandleClientAsync(client, token, ct);
                    }
                    catch (OperationCanceledException) { break; }
                    catch { }
                }
            }, ct);

            StartMdnsResponder(ct);
        }
        catch { }
    }

    private void StartMdnsResponder(CancellationToken ct)
    {
        try
        {
            var mcast = IPAddress.Parse("224.0.0.251");
            var udp = new UdpClient();
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, 5353));
            udp.JoinMulticastGroup(mcast);
            _mdnsUdp = udp;

            string instanceName = SanitizeDnsLabel(Environment.MachineName);
            var localIpv4 = GetPrimaryLanIpv4();
            byte[] announcePacket = BuildMdnsAnnouncementPacket(instanceName, Port, localIpv4);
            var target = new IPEndPoint(mcast, 5353);

            // Periodic multicast announcement + query responder for _yuyue-rec._tcp.local
            _ = Task.Run(async () =>
            {
                try { await udp.SendAsync(announcePacket, announcePacket.Length, target); } catch { }
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        var recvTask = udp.ReceiveAsync(ct).AsTask();
                        var delayTask = Task.Delay(TimeSpan.FromSeconds(45), ct);
                        var winner = await Task.WhenAny(recvTask, delayTask);
                        if (winner == recvTask)
                        {
                            var res = await recvTask;
                            if (ContainsMdnsServiceQuery(res.Buffer, "_yuyue-rec"))
                                await udp.SendAsync(announcePacket, announcePacket.Length, target);
                        }
                        else
                        {
                            await udp.SendAsync(announcePacket, announcePacket.Length, target);
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch { }
                }
            }, ct);
        }
        catch { }
    }

    internal static byte[] BuildMdnsAnnouncementPacket(string instanceLabel, int port, IPAddress ipv4)
    {
        string svc = "_yuyue-rec._tcp.local";
        string fullInstance = $"{SanitizeDnsLabel(instanceLabel)}.{svc}";
        string hostName = $"{SanitizeDnsLabel(instanceLabel)}.local";

        using var ms = new MemoryStream();
        void PutU16(ushort v) { ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)(v & 0xFF)); }
        void PutU32(uint v) { PutU16((ushort)(v >> 16)); PutU16((ushort)(v & 0xFFFF)); }
        void PutName(string name)
        {
            foreach (string lbl in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                byte[] b = Encoding.UTF8.GetBytes(lbl);
                int len = Math.Min(63, b.Length);
                ms.WriteByte((byte)len);
                ms.Write(b, 0, len);
            }
            ms.WriteByte(0);
        }

        // DNS Header: Response + Authoritative (0x8400), 4 answers (PTR, SRV, TXT, A)
        PutU16(0); PutU16(0x8400); PutU16(0); PutU16(4); PutU16(0); PutU16(0);

        // 1. PTR: _yuyue-rec._tcp.local -> <instance>._yuyue-rec._tcp.local
        PutName(svc); PutU16(12); PutU16(1); PutU32(120);
        byte[] ptrData = EncodeDnsName(fullInstance);
        PutU16((ushort)ptrData.Length); ms.Write(ptrData);

        // 2. SRV: <instance> -> host:port
        PutName(fullInstance); PutU16(33); PutU16(0x8001); PutU32(120);
        byte[] hostBytes = EncodeDnsName(hostName);
        PutU16((ushort)(6 + hostBytes.Length));
        PutU16(0); PutU16(0); PutU16((ushort)port);
        ms.Write(hostBytes);

        // 3. TXT: platform=windows
        PutName(fullInstance); PutU16(16); PutU16(0x8001); PutU32(120);
        byte[] txt = Encoding.ASCII.GetBytes("platform=windows");
        PutU16((ushort)(1 + txt.Length));
        ms.WriteByte((byte)txt.Length);
        ms.Write(txt);

        // 4. A: <host>.local -> IPv4
        PutName(hostName); PutU16(1); PutU16(0x8001); PutU32(120);
        byte[] ipBytes = ipv4.GetAddressBytes();
        PutU16(4);
        ms.Write(ipBytes, 0, 4);

        return ms.ToArray();
    }

    private static byte[] EncodeDnsName(string name)
    {
        using var ms = new MemoryStream();
        foreach (string lbl in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            byte[] b = Encoding.UTF8.GetBytes(lbl);
            int len = Math.Min(63, b.Length);
            ms.WriteByte((byte)len);
            ms.Write(b, 0, len);
        }
        ms.WriteByte(0);
        return ms.ToArray();
    }

    internal static bool ContainsMdnsServiceQuery(byte[] packet, string token)
    {
        if (packet.Length < 12) return false;
        // QR bit (0x80) == 0 means query
        if ((packet[2] & 0x80) != 0) return false;
        string ascii = Encoding.ASCII.GetString(packet);
        return ascii.Contains(token, StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizeDnsLabel(string raw)
    {
        var sb = new StringBuilder();
        foreach (char c in raw ?? "yuyue-win")
        {
            if (char.IsLetterOrDigit(c) || c == '-') sb.Append(c);
        }
        return sb.Length > 0 ? sb.ToString()[..Math.Min(32, sb.Length)] : "yuyue-win";
    }

    private static IPAddress GetPrimaryLanIpv4()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address))
                        return ua.Address;
                }
            }
        }
        catch { }
        return IPAddress.Loopback;
    }

    private async Task HandleClientAsync(TcpClient client, string expectedToken, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                using var stream = client.GetStream();
                var buf = new byte[64 * 1024];
                int n = await stream.ReadAsync(buf.AsMemory(0, buf.Length), ct);
                if (n <= 0) return;

                string headText = Encoding.UTF8.GetString(buf, 0, n);
                int headEnd = headText.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (headEnd < 0) return;

                var lines = headText[..headEnd].Split("\r\n");
                var reqParts = lines[0].Split(' ');
                if (reqParts.Length < 2) return;

                string? gotToken = null;
                foreach (var line in lines.Skip(1))
                {
                    int colon = line.IndexOf(':');
                    if (colon > 0 && line[..colon].Trim().Equals("x-yuyue-token", StringComparison.OrdinalIgnoreCase))
                    {
                        gotToken = line[(colon + 1)..].Trim();
                        break;
                    }
                }

                if (gotToken != expectedToken)
                {
                    await WriteResponseAsync(stream, 403, "text/plain", [], ct);
                    return;
                }

                string rawTarget = reqParts[1];
                int qIdx = rawTarget.IndexOf('?');
                string path = qIdx >= 0 ? rawTarget[..qIdx] : rawTarget;
                var query = new Dictionary<string, string>(StringComparer.Ordinal);
                if (qIdx >= 0 && qIdx + 1 < rawTarget.Length)
                {
                    foreach (var pair in rawTarget[(qIdx + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
                    {
                        int eq = pair.IndexOf('=');
                        if (eq > 0) query[Uri.UnescapeDataString(pair[..eq])] = Uri.UnescapeDataString(pair[(eq + 1)..]);
                    }
                }

                if (!query.TryGetValue("id", out var bookId) || string.IsNullOrEmpty(bookId))
                {
                    await WriteResponseAsync(stream, 404, "application/json", [], ct);
                    return;
                }

                var (status, contentType, body) = Respond(path, bookId, query);
                await WriteResponseAsync(stream, status, contentType, body, ct);
            }
            catch { }
        }
    }

    internal (int Status, string Type, byte[] Body) Respond(string path, string bookId, Dictionary<string, string> query)
    {
        try
        {
            if (path == "/card")
            {
                string json = _library.Core.CardsPackBookJson(bookId);
                return (200, "application/json", Encoding.UTF8.GetBytes(string.IsNullOrEmpty(json) ? "[]" : json));
            }
            if (path == "/drama")
            {
                string? manifest = _library.Core.DramaClipManifest(bookId);
                if (string.IsNullOrEmpty(manifest)) return (404, "application/json", []);
                // 1:1 RecordingShare.swift:80 — only expose keys whose rendered files actually exist on disk
                var keys = _library.Core.DramaManifestKeys(manifest)
                    .Where(k => DramaSynthService.IsClipMade(_library.DataDir, bookId, k))
                    .ToArray();
                var payload = JsonSerializer.SerializeToUtf8Bytes(new { manifest, keys });
                return (200, "application/json", payload);
            }
            if (path == "/want")
            {
                uint sec = query.TryGetValue("section", out var ss) && uint.TryParse(ss, out var sv) ? sv : 0u;
                DramaSynthService.EnqueuePhoneWant(_library, bookId, sec);
                return (200, "application/json", "{\"ok\":true}"u8.ToArray());
            }
            if (path == "/clip")
            {
                if (!query.TryGetValue("key", out var key) || string.IsNullOrEmpty(key) || !key.All(char.IsLetterOrDigit))
                    return (404, "application/json", []);
                string clipPath = DramaSynthService.ClipPath(_library.DataDir, bookId, key);
                if (!File.Exists(clipPath)) return (404, "application/json", []);
                byte[] wav = File.ReadAllBytes(clipPath);
                var (samples, rate) = SpeakerPrintService.DecodeWavToMonoFloat(wav);
                if (samples.Length == 0) return (404, "application/json", []);
                // Resample to 48 kHz mono float32 little-endian (1:1 RecordingShare.swift:89-93)
                float[] out48 = rate == 48_000 ? samples : ResampleFloat(samples, rate, 48_000);
                byte[] rawFloat = MemoryMarshal.AsBytes(out48.AsSpan()).ToArray();
                return (200, "application/octet-stream", rawFloat);
            }

            var rec = _library.Core.AudioRecording(bookId);
            string ResolveRecFile(string rawPath) =>
                Path.IsPathRooted(rawPath) ? rawPath : Path.Combine(_library.DataDir, rawPath);

            if (path == "/has")
            {
                var files = rec.Files.Select(f =>
                {
                    string resolved = ResolveRecFile(f.Path);
                    return new
                    {
                        name = Path.GetFileName(resolved),
                        size = File.Exists(resolved) ? new FileInfo(resolved).Length : 0L,
                        seconds = f.Seconds,
                    };
                }).ToArray();
                string title = "";
                try { title = _library.Core.Book(bookId).Summary.Title; } catch { }
                return (200, "application/json", JsonSerializer.SerializeToUtf8Bytes(new { title, files }));
            }
            if (path == "/piece")
            {
                if (!query.TryGetValue("n", out var ns) || !int.TryParse(ns, out int idx) || idx < 0 || idx >= rec.Files.Length)
                    return (404, "application/json", []);
                if (!query.TryGetValue("from", out var fs) || !long.TryParse(fs, out long offset) || offset < 0)
                    return (404, "application/json", []);
                string fp = ResolveRecFile(rec.Files[idx].Path);
                if (!File.Exists(fp)) return (404, "application/json", []);

                using var file = new FileStream(fp, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (offset >= file.Length) return (200, "application/octet-stream", []);
                file.Seek(offset, SeekOrigin.Begin);
                int toRead = (int)Math.Min(PieceBytes, file.Length - offset);
                var chunk = new byte[toRead];
                int actual = file.Read(chunk, 0, toRead);
                return (200, "application/octet-stream", actual == toRead ? chunk : chunk[..actual]);
            }
        }
        catch { }
        return (404, "application/json", []);
    }

    private static float[] ResampleFloat(float[] src, uint fromRate, uint toRate)
    {
        if (src.Length == 0 || fromRate == 0 || fromRate == toRate) return src;
        double ratio = (double)fromRate / toRate;
        int outLen = Math.Max(1, (int)Math.Round(src.Length / ratio));
        var dst = new float[outLen];
        for (int i = 0; i < outLen; i++)
        {
            double pos = i * ratio;
            int idx = (int)pos;
            double frac = pos - idx;
            float s0 = src[Math.Min(idx, src.Length - 1)];
            float s1 = src[Math.Min(idx + 1, src.Length - 1)];
            dst[i] = (float)(s0 + (s1 - s0) * frac);
        }
        return dst;
    }

    private static async Task WriteResponseAsync(NetworkStream stream, int status, string contentType, byte[] body, CancellationToken ct)
    {
        string head = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "No")}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
        byte[] headBytes = Encoding.UTF8.GetBytes(head);
        await stream.WriteAsync(headBytes, ct);
        if (body.Length > 0) await stream.WriteAsync(body, ct);
        await stream.FlushAsync(ct);
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _mdnsUdp?.Dispose(); } catch { }
        try { _listener?.Stop(); } catch { }
        _mdnsUdp = null;
        _listener = null;
    }
}

/// <summary>
/// LAN client companion to <see cref="RecordingShareServer"/> for pulling recordings or drama clips from a peer Mac/PC.
/// </summary>
public static class RecordingShareClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static async Task<(string Manifest, string[] Keys)?> PullDramaManifestAsync(
        string baseUrl, string token, string bookId, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/drama?id={Uri.EscapeDataString(bookId)}");
        req.Headers.TryAddWithoutValidation("x-yuyue-token", token);
        using var resp = await Http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        string manifest = doc.RootElement.TryGetProperty("manifest", out var m) ? m.GetString() ?? "" : "";
        var keys = doc.RootElement.TryGetProperty("keys", out var k) && k.ValueKind == JsonValueKind.Array
            ? k.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToArray()
            : [];
        return string.IsNullOrEmpty(manifest) ? null : (manifest, keys);
    }

    public static async Task<bool> SendWantAsync(string baseUrl, string token, string bookId, uint section, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/want?id={Uri.EscapeDataString(bookId)}&section={section}");
        req.Headers.TryAddWithoutValidation("x-yuyue-token", token);
        using var resp = await Http.SendAsync(req, ct);
        return resp.IsSuccessStatusCode;
    }

    /// <summary>
    /// Pulls pre-rendered drama clip manifest and designed voice cards from a LAN peer and adopts them via
    /// <see cref="YuyueCore.DramaAdoptClips"/> &amp; <see cref="YuyueCore.CardsAdoptBookJson"/>
    /// (parity with Apple <c>RecordingShare.swift</c>).
    /// </summary>
    public static async Task<int> AdoptPeerDramaAsync(
        LibraryService library,
        string baseUrl,
        string token,
        string bookId,
        string peerDeviceName,
        CancellationToken ct = default)
    {
        int downloaded = 0;
        var pulled = await PullDramaManifestAsync(baseUrl, token, bookId, ct);
        if (pulled is { } offer)
        {
            string clipsDir = DramaSynthService.ClipsFolder(library.DataDir, bookId);
            Directory.CreateDirectory(clipsDir);
            foreach (string key in offer.Keys)
            {
                if (DramaSynthService.IsClipMade(library.DataDir, bookId, key)) continue;
                using var clipReq = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/drama-clip?id={Uri.EscapeDataString(bookId)}&key={Uri.EscapeDataString(key)}");
                clipReq.Headers.TryAddWithoutValidation("x-yuyue-token", token);
                using var clipResp = await Http.SendAsync(clipReq, ct);
                if (!clipResp.IsSuccessStatusCode) continue;
                byte[] wav = await clipResp.Content.ReadAsByteArrayAsync(ct);
                if (wav.Length > 0)
                {
                    await File.WriteAllBytesAsync(DramaSynthService.ClipPath(library.DataDir, bookId, key), wav, ct);
                    downloaded++;
                }
            }
            DramaSynthService.AdoptRemoteClips(library, bookId, peerDeviceName, offer.Manifest, offer.Keys);
        }

        // Also adopt designed voice cards for this book if the peer exposes `/card`.
        try
        {
            using var cardReq = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/card?book={Uri.EscapeDataString(bookId)}");
            cardReq.Headers.TryAddWithoutValidation("x-yuyue-token", token);
            using var cardResp = await Http.SendAsync(cardReq, ct);
            if (cardResp.IsSuccessStatusCode)
            {
                string json = await cardResp.Content.ReadAsStringAsync(ct);
                if (!string.IsNullOrWhiteSpace(json))
                    DramaSynthService.AdoptRemoteCardsJson(library, bookId, json);
            }
        }
        catch { }

        return downloaded;
    }
}
