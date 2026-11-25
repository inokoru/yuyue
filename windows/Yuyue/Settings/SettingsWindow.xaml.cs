using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using WinRT.Interop;
using Yuyue.Core;
using Yuyue.Design;
using Yuyue.Reader;
using Yuyue.Speech;
using Path = System.IO.Path;
using TimeSpan = System.TimeSpan;

namespace Yuyue.Settings;

public sealed partial class SettingsWindow : Window
{
    private static readonly string[] SidebarPageOrder =
    [
        "general", "appearance", "reading", "foreign", "listening",
        "companion", "sync", "export", "storage", "data", "about",
    ];

    private readonly LibraryService _library;
    private string _activePage;
    private bool _initializing = true;
    private bool _closed;
    private string? _hoveredThemeMode;

    // 同步页状态
    private bool _editingWebDav;
    private bool _editingGDrive;
    private CancellationTokenSource? _gdriveAuthCts;
    // 朗读页状态 ("" = overview, "kokoro" | "edge" | "free" | "pc" | cloud/local provider keys)
    private string _focusedVoiceSource = "";
    private double _voiceChatScrollOffset;
    private string _voiceBookLang = "zh";
    private bool _voiceSourceConfigOpen;
    private string _selectedInspectorVoiceId = "";
    private string _auditioningVoiceId = "";
    private string _auditioningLineText = "";
    private DateTimeOffset _auditionStartAt;
    private double _auditionEstimatedSecs = 3.0;
    private DispatcherTimer? _voicePlayTimer;
    private readonly HashSet<string> _heardVoiceIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _queuedKokoroVoiceIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _customVoiceLines = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _draftVoiceLines = new(StringComparer.OrdinalIgnoreCase);
    private (string Lang, string OldId, string OldName, string NewName, DateTimeOffset At)? _recentVoiceSwap;
    private bool _localVoiceProbeStarted;

    // 下载进度无损就地刷新签名（P0-3：避免下载百分比 tick 重建整页导致按钮丢失指针捕获）
    private string _lastVoiceStructureSig = "";
    private string _lastFontStructureSig = "";
    private Action? _liveVoiceProgressUpdater;
    private Action? _inspectorVoiceProgressUpdater;
    private Action? _fontProgressUpdater;
    private Action? _fontBookProgressUpdater;
    private Action? _activeFontBookRebuild;

    // 书伴页状态
    private string _focusedCompanionId;
    private bool _companionPickingModel;
    private bool _companionEditingKey;
    private DispatcherTimer? _companionDebounceTimer;
    private CancellationTokenSource? _openRouterAuthCts;
    private readonly HashSet<string> _newlyAddedCompanionIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<string>> _cachedCompanionModels = new();
    private Dictionary<string, IReadOnlyList<string>> _liveLocalCompanions = new();
    private bool _localCompanionProbeStarted;
    // 笔记导出页状态 ("obsidian" | "notion" | "flomo" | "xinzhi")
    private string _focusedExportTarget = "obsidian";
    // 存储页离线扩展模型下载状态
    private CancellationTokenSource? _alignerDlCts;
    private double _alignerDlProgress;
    private Action? _alignerProgressUiUpdater;
    private CancellationTokenSource? _senseVoiceDlCts;
    private double _senseVoiceDlProgress;
    private Action? _senseVoiceProgressUiUpdater;
    // 关于页内联更新检查状态
    private bool _aboutCheckingInline;
    private string? _aboutLastInlineCheckError;
    private bool _aboutJustCheckedUpToDate;
    // 数据快照分项勾选
    private bool _snapProgress = true;
    private bool _snapNotes = true;
    private bool _snapWords = true;
    private bool _snapSettings = true;
    private bool _snapFiles = false;

    // 最近修改记录单例（对齐 MacSettingsHistory.swift：LibraryService.SettingsHistory 单例 + 徽标 + 就地改回/还原）
    private SettingsHistoryStore History => _library.SettingsHistory;
    private DispatcherTimer? _flashTimer;

    public SettingsWindow(LibraryService library, string? initialPage = null)
    {
        _library = library;
        string requested = !string.IsNullOrWhiteSpace(initialPage)
            ? initialPage!
            : _library.Prefs.SettingsLastPage;
        _activePage = ResolvePageKey(requested);

        if (_library.Prefs.ConfiguredAiProviders.Count == 0)
            _library.Prefs.ConfiguredAiProviders.Add(_library.Prefs.AiProvider);
        _focusedCompanionId = _library.Prefs.AiProvider;

        // 兼容迁移旧单 key 到当前 provider key
        string legacyKey = _library.Secrets.Get("ai.apiKey");
        if (!string.IsNullOrEmpty(legacyKey) && string.IsNullOrEmpty(_library.Secrets.Get($"ai.key.{_library.Prefs.AiProvider}")))
            _library.Secrets.Set($"ai.key.{_library.Prefs.AiProvider}", legacyKey);

        InitializeComponent();

        ThemeHelper.ResizeWindow(
            this,
            _library.Prefs.SettingsWindowWidth,
            _library.Prefs.SettingsWindowHeight,
            minWidthDip: 860,
            minHeightDip: 540);
        SetTitleBar(TitleDragRegion);

        InspectorResizeHandle.MinWidthLimit = 252.0;
        InspectorResizeHandle.DefaultWidth = 304.0;
        InspectorResizeHandle.GetCurrentWidth = () => InspectorColumn.Width.Value;
        InspectorResizeHandle.GetMaxWidth = () => Math.Max(252.0, Math.Min(480.0, (RootGrid.ActualWidth > 0 ? RootGrid.ActualWidth : 980.0) - 604.0));
        InspectorResizeHandle.ApplyWidth = w => InspectorColumn.Width = new GridLength(w);
        InspectorResizeHandle.CommitWidth = w =>
        {
            _library.Prefs.SettingsInspectorWidth = w;
            _library.SavePrefs();
        };

        _lastVoiceStructureSig = ComputeVoiceStructureSig();
        _lastFontStructureSig = ComputeFontStructureSig();

        _library.Kokoro.StateChanged += OnLiveVoiceStateChanged;
        _library.LiveVoice.StateChanged += OnLiveVoiceStateChanged;
        _library.Fonts.Changed += OnFontsChanged;
        _library.Sync.StatusChanged += OnSyncStatusChanged;
        _library.Usage.Changed += OnUsageChanged;
        _library.Updates.StateChanged += OnUpdatesStateChanged;
        Closed += (_, _) =>
        {
            _closed = true;
            if (ThemeHelper.TryGetRestoredDipSize(this, out int dipW, out int dipH))
            {
                if (_library.Prefs.SettingsWindowWidth != dipW || _library.Prefs.SettingsWindowHeight != dipH)
                {
                    _library.Prefs.SettingsWindowWidth = dipW;
                    _library.Prefs.SettingsWindowHeight = dipH;
                    _library.SavePrefs();
                }
            }
            _flashTimer?.Stop();
            _companionDebounceTimer?.Stop();
            try { _openRouterAuthCts?.Cancel(); _openRouterAuthCts?.Dispose(); } catch { }
            _openRouterAuthCts = null;
            try { _gdriveAuthCts?.Cancel(); _gdriveAuthCts?.Dispose(); } catch { }
            _gdriveAuthCts = null;
            try { _alignerDlCts?.Cancel(); _alignerDlCts?.Dispose(); } catch { }
            _alignerDlCts = null;
            try { _senseVoiceDlCts?.Cancel(); _senseVoiceDlCts?.Dispose(); } catch { }
            _senseVoiceDlCts = null;
            StopVoicePreviewUi();
            AudioPlayer.StopActivePreview();
            _library.Kokoro.StateChanged -= OnLiveVoiceStateChanged;
            _library.LiveVoice.StateChanged -= OnLiveVoiceStateChanged;
            _library.Fonts.Changed -= OnFontsChanged;
            _library.Sync.StatusChanged -= OnSyncStatusChanged;
            _library.Usage.Changed -= OnUsageChanged;
            _library.Updates.StateChanged -= OnUpdatesStateChanged;
        };

        ApplyAppearanceCore(renderContent: false);
        NavigateTo(_activePage);
        Activated += (_, args) =>
        {
            if (args.WindowActivationState != WindowActivationState.Deactivated)
                _library.Analytics.TrackScreen(_activePage == "about" ? "About" : "Settings");
        };
    }

    private static string ResolvePageKey(string? raw)
    {
        string normalized = (raw ?? "").Trim().ToLowerInvariant() switch
        {
            "speech" => "listening",
            "notes" => "export",
            "update" => "about",
            var s => s,
        };
        return SidebarPageOrder.Contains(normalized) ? normalized : "general";
    }

    private void StopVoicePreviewUi()
    {
        _voicePlayTimer?.Stop();
        _voicePlayTimer = null;
        _auditioningVoiceId = "";
        _auditioningLineText = "";
    }

    private void SetFocusedVoiceSource(string nextSource)
    {
        if (!string.Equals(_focusedVoiceSource, nextSource, StringComparison.OrdinalIgnoreCase))
            _voiceChatScrollOffset = 0;
        _focusedVoiceSource = nextSource;
    }

    private string ComputeVoiceStructureSig()
    {
        if (_library.Kokoro.IsInstalled && _queuedKokoroVoiceIds.Count > 0)
            _queuedKokoroVoiceIds.Clear();
        string koFail = _library.Kokoro.State == KokoroStateKind.Failed ? _library.Kokoro.StatusMessage : "";
        string lvFail = _library.LiveVoice.State == LiveVoiceStateKind.Failed ? _library.LiveVoice.StatusMessage : "";
        string pendZh = _library.Kokoro.GetPendingVoiceName("zh") ?? "";
        string pendEn = _library.Kokoro.GetPendingVoiceName("en") ?? "";
        return $"{_library.Kokoro.State}|{_library.Kokoro.IsInstalled}|{koFail}|{pendZh}|{pendEn}|{_library.LiveVoice.State}|{_library.LiveVoice.IsReady}|{lvFail}|{_library.LiveVoice.GetClonedCards().Count}";
    }

    private string ComputeFontStructureSig()
    {
        string cats = string.Join("|", _library.Fonts.Catalog.Select(c =>
            $"{c.Id}:{(_library.Fonts.IsPackInstalled(c.Id) ? 1 : 0)}:{(_library.Fonts.IsDownloading(c.Id) ? 1 : 0)}"));
        return $"{cats}#c:{_library.Fonts.GetCustomFontFiles().Count}";
    }

    private void UpdateHistoryBadges()
    {
        int today = History.TodayCount;
        bool show = today > 0;
        string text = today > 99 ? "99+" : today.ToString();
        var tint = CurrentTint;
        foreach (var (badge, lbl) in new[]
        {
            (CornerHistoryBadge, CornerHistoryCountText),
            (InspectorHistoryBadge, InspectorHistoryCountText),
        })
        {
            badge.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            badge.Background = ThemeHelper.Brush(tint.Fill);
            lbl.Text = text;
            lbl.Foreground = new SolidColorBrush(Colors.White);
        }
    }

    private void ShowFlashBanner(string msg)
    {
        SavedFlashText.Text = msg;
        CenterSavedFlashText.Text = msg;

        _flashTimer?.Stop();
        _flashTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1800) };
        _flashTimer.Tick += (_, _) =>
        {
            _flashTimer.Stop();
            SavedFlashText.Text = "";
            CenterSavedFlashText.Text = "";
        };
        _flashTimer.Start();
    }

    private void RecordChange(
        string title,
        string value,
        Action? undo = null,
        Action? redo = null,
        string? oldValue = null,
        string? prefKey = null,
        string? oldRawJson = null,
        string? newRawJson = null,
        string? pageOverride = null)
    {
        History.Record(
            pageOverride ?? _activePage,
            title,
            value,
            oldValue,
            undo,
            redo,
            prefKey: prefKey,
            oldRawJson: oldRawJson,
            newRawJson: newRawJson);
        UpdateHistoryBadges();
        ShowFlashBanner($"✓ 已保存 · {title} {value}");
    }

    private void RecordPrefChange<T>(
        string prefPropertyName,
        string title,
        string newDisplay,
        string? oldDisplay,
        T oldVal,
        T newVal,
        Action? undo = null,
        Action? redo = null,
        string? pageOverride = null)
    {
        string oldJson = System.Text.Json.JsonSerializer.Serialize(oldVal);
        string newJson = System.Text.Json.JsonSerializer.Serialize(newVal);
        RecordChange(
            title,
            newDisplay,
            undo,
            redo,
            oldValue: oldDisplay,
            prefKey: $"prefs:{prefPropertyName}",
            oldRawJson: oldJson,
            newRawJson: newJson,
            pageOverride: pageOverride);
    }

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (YYChrome.HasBlockingModalOrFlyoutOpen(RootGrid.XamlRoot)) return;

        bool ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
        bool shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
        bool alt = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(CoreVirtualKeyStates.Down);

        if (ctrl && !shift && !alt && e.Key == VirtualKey.W)
        {
            e.Handled = true;
            Close();
            return;
        }

        if (!ctrl && !shift && !alt && e.Key == VirtualKey.Escape)
        {
            if (_activePage == "listening" && _voiceSourceConfigOpen)
            {
                _voiceSourceConfigOpen = false;
                RenderDynamicPage("listening");
                RenderInspector("listening");
                e.Handled = true;
                return;
            }
            if (_activePage == "listening" && !string.IsNullOrEmpty(_focusedVoiceSource))
            {
                SetFocusedVoiceSource("");
                _voiceSourceConfigOpen = false;
                StopVoicePreviewUi();
                AudioPlayer.StopActivePreview();
                NavigateTo("listening");
                e.Handled = true;
                return;
            }
            if (_activePage == "companion" && _companionPickingModel)
            {
                _companionPickingModel = false;
                RenderInspector("companion");
                e.Handled = true;
                return;
            }
            if (_activePage == "companion" && _companionEditingKey)
            {
                _companionEditingKey = false;
                RenderInspector("companion");
                e.Handled = true;
                return;
            }
            if (_activePage == "sync" && _editingWebDav)
            {
                _editingWebDav = false;
                RenderDynamicPage("sync");
                e.Handled = true;
                return;
            }
            if (_activePage == "sync" && _editingGDrive)
            {
                try { _gdriveAuthCts?.Cancel(); } catch { }
                _editingGDrive = false;
                RenderDynamicPage("sync");
                e.Handled = true;
                return;
            }

            e.Handled = true;
            Close();
            return;
        }

        if (e.OriginalSource is TextBox or PasswordBox or RichEditBox) return;
        if (RootGrid.XamlRoot is not null && FocusManager.GetFocusedElement(RootGrid.XamlRoot) is TextBox or PasswordBox or RichEditBox) return;

        if (ctrl && !alt)
        {
            if (!shift && e.Key is >= VirtualKey.Number1 and <= VirtualKey.Number9)
            {
                int idx = (int)e.Key - (int)VirtualKey.Number1;
                if (idx >= 0 && idx < SidebarPageOrder.Length)
                {
                    NavigateTo(SidebarPageOrder[idx]);
                    e.Handled = true;
                }
                return;
            }

            if ((e.Key == VirtualKey.Z && shift) || e.Key == VirtualKey.Y)
            {
                if (History.TryRedo() is { } redone)
                {
                    UpdateHistoryBadges();
                    ShowFlashBanner($"↷ 已还原 · {redone.Title} {redone.NewValue}");
                    ApplyAppearance();
                    e.Handled = true;
                }
                return;
            }

            if (e.Key == VirtualKey.Z)
            {
                if (History.TryUndo() is { } undone)
                {
                    UpdateHistoryBadges();
                    ShowFlashBanner($"↶ 已改回 · {undone.Title}");
                    ApplyAppearance();
                    e.Handled = true;
                }
                return;
            }
        }

        if (!ctrl && !shift && !alt && e.Key is VirtualKey.Up or VirtualKey.Down)
        {
            int curIdx = Array.IndexOf(SidebarPageOrder, _activePage);
            if (curIdx >= 0)
            {
                int nextIdx = Math.Clamp(curIdx + (e.Key == VirtualKey.Down ? 1 : -1), 0, SidebarPageOrder.Length - 1);
                if (nextIdx != curIdx)
                {
                    NavigateTo(SidebarPageOrder[nextIdx]);
                    e.Handled = true;
                }
            }
        }
    }

    private void HistoryBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement anchor) return;
        Flyout? flyoutRef = null;
        string selectedPageFilter = "all";
        bool confirmingClear = false;
        string? feedbackMessage = null;

        var root = new StackPanel
        {
            Width = 360,
            Spacing = 0,
            Padding = new Thickness(16, 15, 16, 12),
            Background = ThemeHelper.Brush(CurrentPalette.Bg),
        };
        var bodyHost = new StackPanel { Spacing = 10 };

        string DayHeading(DateTime localDate)
        {
            var d = localDate.Date;
            if (d == DateTime.Today) return "今天";
            if (d == DateTime.Today.AddDays(-1)) return "昨天";
            return d.Year == DateTime.Today.Year ? $"{d.Month}月{d.Day}日" : $"{d.Year}年{d.Month}月{d.Day}日";
        }

        Color? TryResolvePaperDotColor(string val)
        {
            return val.Trim() switch
            {
                "素白" => YYPapers.Plain.Bg,
                "暗黑" => YYPapers.Dark.Bg,
                "纸本" => YYPapers.Kami.Bg,
                "纸本 · 夜" => YYPapers.Kaminight.Bg,
                "芦苇" => YYPapers.Sage.Bg,
                "铅纸" => YYPapers.Eink.Bg,
                _ => TryFindCustomPaperColor(val.Trim()),
            };
        }

        Color? TryFindCustomPaperColor(string name)
        {
            try
            {
                var cp = _library.Core.CustomPapers().FirstOrDefault(x => x.Name == name);
                if (cp is not null)
                {
                    var pc = YuyueCoreMethods.PaperColors(cp);
                    return CustomPaperDialog.ParseHexColor(pc.Bg, YYPapers.Kami.Bg);
                }
            }
            catch { }
            return null;
        }

        string? TryResolveFontPreviewFamily(string fontLabel)
        {
            string t = fontLabel.Trim();
            return t switch
            {
                "系统" or "黑体" => YYFontFamily.Sans.WindowsFamily(),
                "宋体" => YYFontFamily.Serif.WindowsFamily(),
                "楷体" => YYFontFamily.Kaiti.WindowsFamily(),
                "霞鹜文楷" => _library.Fonts.ResolveXamlFontSource("wenkai", YYFontFamily.Wenkai.WindowsFamily()),
                "思源宋体" => _library.Fonts.ResolveXamlFontSource("notoserif", YYFontFamily.Serif.WindowsFamily()),
                "思源黑体" => _library.Fonts.ResolveXamlFontSource("notosans", YYFontFamily.Sans.WindowsFamily()),
                _ => $"{t}, {YYFontFamily.Serif.WindowsFamily()}",
            };
        }

        UIElement BuildValueAtom(SettingsHistoryRecord rec, string val, YYPalette p, YYTintColors tint, bool muted, bool strikethrough)
        {
            string trimmed = val.Trim();
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };

            if ((trimmed is "开" or "关" or "On" or "Off") && !rec.Title.Contains("纸色", StringComparison.Ordinal))
            {
                bool isOnVal = trimmed is "开" or "On";
                var knob = new Ellipse
                {
                    Width = 7,
                    Height = 7,
                    Fill = new SolidColorBrush(Colors.White),
                    HorizontalAlignment = isOnVal ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                row.Children.Add(new Border
                {
                    Width = 16,
                    Height = 10,
                    CornerRadius = new CornerRadius(5),
                    Padding = new Thickness(1.5, 0, 1.5, 0),
                    Background = ThemeHelper.Brush(isOnVal ? tint.Fill : p.Line),
                    Opacity = muted ? 0.55 : 1.0,
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = knob,
                });
            }
            else if (rec.Title.Contains("纸色", StringComparison.Ordinal) && TryResolvePaperDotColor(trimmed) is Color paperCol)
            {
                row.Children.Add(new Border
                {
                    Width = 10,
                    Height = 10,
                    CornerRadius = new CornerRadius(5),
                    Background = ThemeHelper.Brush(paperCol),
                    BorderBrush = ThemeHelper.Brush(p.Line),
                    BorderThickness = new Thickness(0.75),
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }

            var tb = new TextBlock
            {
                Text = trimmed,
                FontSize = 11.5,
                FontWeight = muted ? Microsoft.UI.Text.FontWeights.Normal : Microsoft.UI.Text.FontWeights.Medium,
                Foreground = ThemeHelper.Brush(muted ? p.T3 : p.T1),
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (strikethrough)
                tb.TextDecorations = Windows.UI.Text.TextDecorations.Strikethrough;
            if (rec.Title == "字体" && TryResolveFontPreviewFamily(trimmed) is string fFam)
                tb.FontFamily = new FontFamily(fFam);

            row.Children.Add(tb);
            return row;
        }

        void RefreshHostAfterActuate()
        {
            UpdateHistoryBadges();
            ApplyAppearance();
        }

        void RebuildHistoryFlyout()
        {
            var p = CurrentPalette;
            var tint = CurrentTint;
            root.Background = ThemeHelper.Brush(p.Bg);
            bodyHost.Children.Clear();

            var allEntries = History.Entries;

            // Header
            var head = new Grid();
            var headLeft = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            headLeft.Children.Add(new TextBlock
            {
                Text = "最近改过",
                FontSize = 15,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = ThemeHelper.Brush(p.T1),
                VerticalAlignment = VerticalAlignment.Center,
            });
            headLeft.Children.Add(new TextBlock
            {
                Text = allEntries.Count == 0 ? "暂无记录" : $"{allEntries.Count} 项记录",
                FontSize = 11.5,
                Foreground = ThemeHelper.Brush(p.T3),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 1, 0, 0),
            });
            head.Children.Add(headLeft);

            if (!string.IsNullOrEmpty(feedbackMessage))
            {
                head.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(9),
                    Background = ThemeHelper.Brush(tint.Soft),
                    Padding = new Thickness(8, 2, 8, 2),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = feedbackMessage,
                        FontSize = 10.5,
                        FontWeight = Microsoft.UI.Text.FontWeights.Medium,
                        Foreground = ThemeHelper.Brush(tint.Accent),
                        MaxLines = 1,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    },
                });
            }
            bodyHost.Children.Add(head);

            if (allEntries.Count == 0)
            {
                var emptyCenter = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 22, 0, 24) };
                emptyCenter.Children.Add(new Border
                {
                    Width = 44,
                    Height = 44,
                    CornerRadius = new CornerRadius(12),
                    Background = ThemeHelper.Brush(p.Card),
                    BorderBrush = ThemeHelper.Brush(p.Line),
                    BorderThickness = new Thickness(0.75),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 2),
                    Child = YYIcons.Make("clock-history", 20, ThemeHelper.Brush(p.T2)),
                });
                emptyCenter.Children.Add(new TextBlock
                {
                    Text = "还没有修改记录",
                    FontSize = 14,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = ThemeHelper.Brush(p.T1),
                    HorizontalAlignment = HorizontalAlignment.Center,
                });
                emptyCenter.Children.Add(new TextBlock
                {
                    Text = "调整过的设置会记在这里，\n每一项都能随时改回去。",
                    FontSize = 12,
                    Foreground = ThemeHelper.Brush(p.T3),
                    TextAlignment = TextAlignment.Center,
                    LineHeight = 19,
                    HorizontalAlignment = HorizontalAlignment.Center,
                });
                bodyHost.Children.Add(emptyCenter);

                bodyHost.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(-4, 0, -4, 2) });
                var emptyFoot = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Opacity = 0.68, Margin = new Thickness(0, 2, 0, 0) };
                emptyFoot.Children.Add(YYChrome.MakeKeycap("Ctrl+Z", IsDark));
                emptyFoot.Children.Add(new TextBlock { Text = "无可改回项", FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Center });
                bodyHost.Children.Add(emptyFoot);
                L10n.ApplyTree(bodyHost);
                return;
            }

            // Dynamic per-page filter chips with counts (shown when >= 2 pages have entries)
            var pageCounts = SidebarPageOrder
                .Select(pg => (Page: pg, Count: allEntries.Count(e => e.Page == pg)))
                .Where(x => x.Count > 0)
                .ToArray();

            if (selectedPageFilter != "all" && pageCounts.All(x => x.Page != selectedPageFilter))
                selectedPageFilter = "all";

            if (pageCounts.Length >= 2)
            {
                var chipRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
                void AddFilterChip(string key, string label, int count)
                {
                    bool active = selectedPageFilter == key;
                    var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
                    sp.Children.Add(new TextBlock
                    {
                        Text = label,
                        FontSize = 11,
                        FontWeight = active ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                        Foreground = ThemeHelper.Brush(active ? tint.Accent : p.T2),
                    });
                    sp.Children.Add(new TextBlock
                    {
                        Text = count.ToString(),
                        FontSize = 10,
                        Foreground = ThemeHelper.Brush(active ? tint.Accent : p.T3),
                        VerticalAlignment = VerticalAlignment.Center,
                    });
                    var chip = new Button
                    {
                        Height = 24,
                        MinHeight = 24,
                        Padding = new Thickness(9, 0, 9, 0),
                        CornerRadius = new CornerRadius(12),
                        BorderThickness = new Thickness(0.75),
                        Background = ThemeHelper.Brush(active ? tint.Soft : p.Card),
                        BorderBrush = ThemeHelper.Brush(active ? tint.Edge : p.Line),
                        UseSystemFocusVisuals = false,
                        Content = sp,
                    };
                    string captured = key;
                    chip.Click += (_, _) =>
                    {
                        selectedPageFilter = captured;
                        confirmingClear = false;
                        RebuildHistoryFlyout();
                    };
                    chipRow.Children.Add(chip);
                }

                AddFilterChip("all", "全部", allEntries.Count);
                foreach (var (pg, cnt) in pageCounts)
                    AddFilterChip(pg, PageNameOf(pg), cnt);

                bodyHost.Children.Add(new ScrollViewer
                {
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                    HorizontalScrollMode = ScrollMode.Auto,
                    VerticalScrollMode = ScrollMode.Disabled,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Content = chipRow,
                });
            }

            var filtered = (selectedPageFilter == "all"
                ? allEntries
                : allEntries.Where(h => h.Page == selectedPageFilter)).Take(40).ToArray();

            // Day-grouped white p.Card sections ("今天", "昨天", "M月d日")
            var groupsStack = new StackPanel { Spacing = 10 };
            foreach (var dayGroup in filtered.GroupBy(e => e.Time.LocalDateTime.Date))
            {
                var dayWrap = new StackPanel { Spacing = 4 };
                dayWrap.Children.Add(new TextBlock
                {
                    Text = DayHeading(dayGroup.Key),
                    FontSize = 11,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = ThemeHelper.Brush(p.T3),
                    Margin = new Thickness(4, 0, 0, 0),
                });

                var cardRows = new StackPanel { Spacing = 0 };
                var items = dayGroup.ToArray();
                for (int i = 0; i < items.Length; i++)
                {
                    if (i > 0)
                    {
                        cardRows.Children.Add(new Border
                        {
                            Height = 0.5,
                            Background = ThemeHelper.Brush(p.Line),
                            Margin = new Thickness(46, 0, 12, 0),
                        });
                    }

                    var entry = items[i];
                    var rowGrid = new Grid
                    {
                        ColumnSpacing = 10,
                        Padding = new Thickness(10, 8, 10, 8),
                        Background = new SolidColorBrush(Colors.Transparent),
                    };
                    rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
                    rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    // Left page icon tile
                    var iconTile = new Border
                    {
                        Width = 28,
                        Height = 28,
                        CornerRadius = new CornerRadius(7),
                        Background = ThemeHelper.Brush(p.Inset),
                        VerticalAlignment = VerticalAlignment.Center,
                        Opacity = entry.Undone ? 0.55 : 1.0,
                        Child = YYIcons.Make(PageIconKeyOf(entry.Page), 13, ThemeHelper.Brush(p.T2)),
                    };
                    rowGrid.Children.Add(iconTile);

                    // Middle title + page jump + value change
                    var midStack = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
                    var topLine = new Grid { ColumnSpacing = 6 };
                    topLine.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    topLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var titleWrap = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
                    titleWrap.Children.Add(new TextBlock
                    {
                        Text = entry.Title,
                        FontSize = 12,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        Foreground = ThemeHelper.Brush(entry.Undone ? p.T3 : p.T1),
                        MaxLines = 1,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        VerticalAlignment = VerticalAlignment.Center,
                    });

                    var pageJumpBtn = new Button
                    {
                        Background = new SolidColorBrush(Colors.Transparent),
                        BorderThickness = new Thickness(0),
                        Padding = new Thickness(3, 0, 3, 0),
                        MinWidth = 0,
                        MinHeight = 0,
                        UseSystemFocusVisuals = false,
                        VerticalAlignment = VerticalAlignment.Center,
                        Content = new TextBlock
                        {
                            Text = $"{PageNameOf(entry.Page)} ›",
                            FontSize = 10.5,
                            Foreground = ThemeHelper.Brush(p.T3),
                        },
                    };
                    pageJumpBtn.Click += (_, _) =>
                    {
                        flyoutRef?.Hide();
                        NavigateTo(entry.Page);
                    };
                    titleWrap.Children.Add(pageJumpBtn);
                    topLine.Children.Add(titleWrap);

                    var timeTb = new TextBlock
                    {
                        Text = entry.Time.LocalDateTime.ToString("HH:mm"),
                        FontSize = 10.5,
                        Foreground = ThemeHelper.Brush(p.T3),
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    Grid.SetColumn(timeTb, 1);
                    topLine.Children.Add(timeTb);
                    midStack.Children.Add(topLine);

                    // Value Old -> New line
                    var valLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
                    if (!string.IsNullOrWhiteSpace(entry.OldValue))
                    {
                        valLine.Children.Add(BuildValueAtom(entry, entry.OldValue!, p, tint, muted: !entry.Undone, strikethrough: false));
                        valLine.Children.Add(new TextBlock
                        {
                            Text = "→",
                            FontSize = 10.5,
                            Foreground = ThemeHelper.Brush(p.T3),
                            VerticalAlignment = VerticalAlignment.Center,
                        });
                    }
                    valLine.Children.Add(BuildValueAtom(entry, entry.NewValue, p, tint, muted: entry.Undone, strikethrough: entry.Undone));
                    midStack.Children.Add(valLine);

                    Grid.SetColumn(midStack, 1);
                    rowGrid.Children.Add(midStack);

                    // Right in-place ToggleInPlace button ("↶ 改回" / "↷ 还原")
                    bool canActuate = History.CanActuate(entry, applyUndo: !entry.Undone);
                    var actSlot = new Grid { MinWidth = 56, VerticalAlignment = VerticalAlignment.Center };
                    if (canActuate)
                    {
                        var actBtn = new Button
                        {
                            Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
                            Content = entry.Undone ? "↷ 还原" : "↶ 改回",
                            FontSize = 11,
                            Height = 24,
                            MinHeight = 24,
                            Padding = new Thickness(8, 0, 8, 0),
                            HorizontalAlignment = HorizontalAlignment.Right,
                        };
                        if (entry.Undone)
                            actBtn.Foreground = ThemeHelper.Brush(tint.Accent);

                        actBtn.Click += (_, _) =>
                        {
                            if (History.ToggleInPlace(entry))
                            {
                                feedbackMessage = entry.Undone
                                    ? $"✓ 已改回「{entry.Title}」"
                                    : $"✓ 已还原「{entry.Title}」";
                                confirmingClear = false;
                                RefreshHostAfterActuate();
                                RebuildHistoryFlyout();
                            }
                        };
                        actSlot.Children.Add(actBtn);
                    }
                    Grid.SetColumn(actSlot, 2);
                    rowGrid.Children.Add(actSlot);

                    cardRows.Children.Add(rowGrid);
                }

                dayWrap.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(12),
                    Background = ThemeHelper.Brush(p.Card),
                    BorderBrush = ThemeHelper.Brush(IsDark ? Color.FromArgb(16, 255, 255, 255) : p.Line),
                    BorderThickness = new Thickness(0.5),
                    Child = cardRows,
                });
                groupsStack.Children.Add(dayWrap);
            }

            bodyHost.Children.Add(new ScrollViewer
            {
                MaxHeight = 320,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = groupsStack,
            });

            // Footer: [Ctrl+Z] 改回「…」 | 2-step inline 清空记录 confirmation
            bodyHost.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(-4, 2, -4, 0) });

            if (confirmingClear)
            {
                var confirmBar = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 2, 0, 0) };
                confirmBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                confirmBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                confirmBar.Children.Add(new TextBlock
                {
                    Text = "清空记录？当前设置会保留",
                    FontSize = 11.5,
                    Foreground = ThemeHelper.Brush(p.T2),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                var confirmBtns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                var cancelClrBtn = new Button
                {
                    Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
                    Content = "取消",
                    Height = 24,
                    MinHeight = 24,
                    Padding = new Thickness(9, 0, 9, 0),
                    FontSize = 11,
                };
                cancelClrBtn.Click += (_, _) =>
                {
                    confirmingClear = false;
                    RebuildHistoryFlyout();
                };
                var doClrBtn = new Button
                {
                    Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
                    Content = "清空",
                    Height = 24,
                    MinHeight = 24,
                    Padding = new Thickness(9, 0, 9, 0),
                    FontSize = 11,
                    Foreground = ThemeHelper.Brush(p.Danger),
                };
                doClrBtn.Click += (_, _) =>
                {
                    History.Clear();
                    UpdateHistoryBadges();
                    confirmingClear = false;
                    feedbackMessage = null;
                    RebuildHistoryFlyout();
                };
                confirmBtns.Children.Add(cancelClrBtn);
                confirmBtns.Children.Add(doClrBtn);
                Grid.SetColumn(confirmBtns, 1);
                confirmBar.Children.Add(confirmBtns);
                bodyHost.Children.Add(confirmBar);
            }
            else
            {
                var foot = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 2, 0, 0) };
                foot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var latestUndoable = allEntries.FirstOrDefault(r => !r.Undone && History.CanActuate(r, applyUndo: true));
                var undoKeyStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
                undoKeyStack.Children.Add(YYChrome.MakeKeycap("Ctrl+Z", IsDark));
                undoKeyStack.Children.Add(new TextBlock
                {
                    Text = latestUndoable is not null ? $"改回「{latestUndoable.Title}」" : "无可改回项",
                    FontSize = 11.5,
                    Foreground = ThemeHelper.Brush(latestUndoable is not null ? p.T2 : p.T3),
                    VerticalAlignment = VerticalAlignment.Center,
                    MaxLines = 1,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });

                var quickUndoBtn = new Button
                {
                    Background = new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(2, 2, 6, 2),
                    MinHeight = 24,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    IsEnabled = latestUndoable is not null,
                    Opacity = latestUndoable is not null ? 1.0 : 0.65,
                    UseSystemFocusVisuals = false,
                    Content = undoKeyStack,
                };
                quickUndoBtn.Click += (_, _) =>
                {
                    if (latestUndoable is not null && History.ToggleInPlace(latestUndoable))
                    {
                        feedbackMessage = $"✓ 已改回「{latestUndoable.Title}」";
                        RefreshHostAfterActuate();
                        RebuildHistoryFlyout();
                    }
                };
                foot.Children.Add(quickUndoBtn);

                var clrBtn = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Background = new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(6, 2, 6, 2),
                    FontSize = 11.5,
                    Foreground = ThemeHelper.Brush(p.T3),
                    UseSystemFocusVisuals = false,
                    Content = "清空记录",
                };
                clrBtn.Click += (_, _) =>
                {
                    confirmingClear = true;
                    RebuildHistoryFlyout();
                };
                Grid.SetColumn(clrBtn, 1);
                foot.Children.Add(clrBtn);
                bodyHost.Children.Add(foot);
            }

            L10n.ApplyTree(bodyHost);
        }

        RebuildHistoryFlyout();
        root.Children.Add(bodyHost);

        flyoutRef = new Flyout
        {
            FlyoutPresenterStyle = (Style)Application.Current.Resources["YYFloatFlyoutPresenterStyle"],
            Content = root,
        };
        flyoutRef.ShowAt(anchor);
    }

    private static string PageIconKeyOf(string p) => p switch
    {
        "general" => "sliders",
        "appearance" => "sun",
        "reading" => "book-open",
        "foreign" => "char-book",
        "listening" => "headphones",
        "companion" => "bubble-lines",
        "sync" => "sync-arrows",
        "export" => "share-up",
        "storage" => "drive",
        "data" => "cube",
        _ => "info-circle",
    };

    private static string PageNameOf(string p) => p switch
    {
        "general" => "通用",
        "appearance" => "外观",
        "reading" => "阅读",
        "foreign" => "外文",
        "listening" => "朗读",
        "companion" => "书伴",
        "sync" => "同步",
        "export" => "笔记导出",
        "storage" => "存储",
        "data" => "数据",
        _ => "关于",
    };

    private void OnLiveVoiceStateChanged()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_closed) return;
            _liveVoiceProgressUpdater?.Invoke();
            _inspectorVoiceProgressUpdater?.Invoke();
            string sig = ComputeVoiceStructureSig();
            if (sig == _lastVoiceStructureSig) return;
            _lastVoiceStructureSig = sig;
            if (_activePage == "listening")
            {
                RenderDynamicPage("listening");
                RenderInspector("listening");
            }
        });
    }

    private void OnFontsChanged()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_closed) return;
            _fontProgressUpdater?.Invoke();
            _fontBookProgressUpdater?.Invoke();
            string sig = ComputeFontStructureSig();
            if (sig == _lastFontStructureSig) return;
            _lastFontStructureSig = sig;
            _activeFontBookRebuild?.Invoke();
            if (_activePage == "reading")
            {
                RenderDynamicPage("reading");
                RenderInspector("reading");
            }
        });
    }

    private void OnSyncStatusChanged()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_closed) return;
            if (_activePage == "sync" && !_editingWebDav && !_editingGDrive)
                RenderDynamicPage("sync");
        });
    }

    private void OnUsageChanged()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_closed) return;
            if (_activePage == "companion")
                RenderDynamicPage("companion");
        });
    }

    private void OnUpdatesStateChanged()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_closed) return;
            MountIcons();
            if (_activePage == "about" && !_aboutCheckingInline)
                RenderDynamicPage("about");
        });
    }

    private static bool HasInspector(string page) =>
        page is "reading" or "foreign" or "listening" or "companion" or "export" or "storage" or "data";

    private void MountIcons()
    {
        var p = CurrentPalette;
        var accent = ThemeHelper.Brush(CurrentTint.Accent);
        var t2 = ThemeHelper.Brush(p.T2);
        Brush B(string id) => _activePage == id ? accent : t2;

        // 1:1 MacSettings.swift lines 250-264（Apple SF Symbol "book" 为摊开的书本 book-open）
        IconGeneral.Content = YYIcons.Make("sliders", 14, B("general"));
        IconAppearance.Content = YYIcons.Make("sun", 14.5, B("appearance"));
        IconReading.Content = YYIcons.Make("book-open", 14.5, B("reading"));
        IconForeign.Content = YYIcons.Make("char-book", 14, B("foreign"));
        IconListening.Content = YYIcons.Make("headphones", 14, B("listening"));
        IconCompanion.Content = YYIcons.Make("bubble-lines", 14, B("companion"));
        IconSync.Content = YYIcons.Make("sync-arrows", 14, B("sync"));
        IconExport.Content = YYIcons.Make("share-up", 14, B("export"));
        IconStorage.Content = YYIcons.Make("drive", 14, B("storage"));
        IconData.Content = YYIcons.Make("cube", 14, B("data"));
        IconAbout.Content = YYIcons.Make("info-circle", 14, B("about"));
        AboutUpdateDot.Visibility = _library.Updates.HasUnreadBadge ? Visibility.Visible : Visibility.Collapsed;

        CornerHistoryBtn.Content = YYIcons.Make("clock-history", 14.5, t2);
        InspectorHistoryBtn.Content = YYIcons.Make("clock-history", 14, t2);
    }

    private void RefreshThemeModePreviews()
    {
        var palette = CurrentPalette;
        var accent = ThemeHelper.Brush(CurrentTint.Accent);
        var clear = new SolidColorBrush(Colors.Transparent);
        var line = ThemeHelper.Brush(palette.Line);
        var t1 = ThemeHelper.Brush(palette.T1);
        var t2 = ThemeHelper.Brush(palette.T2);

        void PaintMode(string key, Border box, TextBlock lbl)
        {
            bool on = _library.Prefs.ThemeMode == key;
            bool hovered = !on && _hoveredThemeMode == key;
            box.BorderBrush = on ? accent : (hovered ? line : clear);
            box.BorderThickness = new Thickness(on ? 2 : (hovered ? 1 : 0));
            box.Padding = new Thickness(on ? 1 : (hovered ? 2 : 3));
            lbl.Foreground = on ? t1 : t2;
            lbl.FontWeight = on ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        }

        PaintMode("light", ThemeLightPreview, ThemeLightLabel);
        PaintMode("dark", ThemeDarkPreview, ThemeDarkLabel);
        PaintMode("system", ThemeSystemPreview, ThemeSystemLabel);
    }

    public void ApplyAppearance() => ApplyAppearanceCore(renderContent: true);

    private void ApplyAppearanceCore(bool renderContent)
    {
        Title = L10n.T("设置");
        _initializing = true;
        RootGrid.RequestedTheme = _library.Prefs.ToElementTheme();
        bool dark = IsDark;
        ThemeHelper.ConfigureTitleBar(this, dark);
        ThemeHelper.ApplyControlThemeResources(RootGrid.Resources, dark, _library.Prefs.Tint);
        MountIcons();
        UpdateHistoryBadges();

        var palette = CurrentPalette;
        var accent = ThemeHelper.Brush(CurrentTint.Accent);
        var clear = new SolidColorBrush(Colors.Transparent);
        var t1 = ThemeHelper.Brush(palette.T1);
        var t2 = ThemeHelper.Brush(palette.T2);

        RefreshThemeModePreviews();

        void PaintDot(Border dot, TextBlock lbl, bool on)
        {
            dot.BorderBrush = on ? accent : clear;
            lbl.Foreground = on ? t1 : t2;
            lbl.FontWeight = on ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        }
        PaintDot(TintDotCamel, TintLabelCamel, _library.Prefs.TintId == "camel");
        PaintDot(TintDotSage, TintLabelSage, _library.Prefs.TintId == "sage");
        PaintDot(TintDotMist, TintLabelMist, _library.Prefs.TintId == "mist");
        PaintDot(TintDotBlush, TintLabelBlush, _library.Prefs.TintId == "blush");

        RenderAppIconChoices();

        CoverSizeSlider.Value = Math.Clamp(_library.Prefs.CoverWidth, 80, 130);
        ShowProgressToggle.IsOn = _library.Prefs.ShowProgress;
        AutomationProperties.SetName(CoverSizeSlider, L10n.T("书架封面大小"));
        AutomationProperties.SetName(ShowProgressToggle, L10n.T("封面下显示进度"));
        AutomationProperties.SetName(CornerHistoryBtn, L10n.T("最近修改的设置"));
        AutomationProperties.SetName(InspectorHistoryBtn, L10n.T("最近修改的设置"));
        _initializing = false;

        if (!renderContent) return;

        UpdateMiddleHostVisibility(_activePage);
        if (_activePage != "appearance") RenderDynamicPage(_activePage);
        if (HasInspector(_activePage)) RenderInspector(_activePage);
        L10n.ApplyTree(RootGrid);
    }

    private void UpdateMiddleHostVisibility(string page)
    {
        bool inVoiceSource = page == "listening" && !string.IsNullOrEmpty(_focusedVoiceSource);
        VoiceSourceHost.Visibility = inVoiceSource ? Visibility.Visible : Visibility.Collapsed;
        MainScrollViewer.Visibility = inVoiceSource ? Visibility.Collapsed : Visibility.Visible;
        PageHeaderGrid.Visibility = (page == "about" || inVoiceSource) ? Visibility.Collapsed : Visibility.Visible;
        AppearancePage.Visibility = page == "appearance" ? Visibility.Visible : Visibility.Collapsed;
        DynamicPageHost.Visibility = page == "appearance" ? Visibility.Collapsed : Visibility.Visible;
    }

    public void NavigateTo(string page)
    {
        page = ResolvePageKey(page);
        if (_activePage == "listening" && page != "listening")
        {
            StopVoicePreviewUi();
            AudioPlayer.StopActivePreview();
        }

        _activePage = page;
        _library.Analytics.TrackScreen(page == "about" ? "About" : "Settings");
        _companionPickingModel = false;
        _companionEditingKey = false;
        if (_library.Prefs.SettingsLastPage != page)
        {
            _library.Prefs.SettingsLastPage = page;
            _library.SavePrefs();
        }

        var selectedBg = (Brush)Application.Current.Resources["YYSidebarSelectedBrush"];
        var clear = new SolidColorBrush(Colors.Transparent);

        Button[] navs = [NavGeneral, NavAppearance, NavReading, NavForeign, NavListening, NavCompanion, NavSync, NavExport, NavStorage, NavData, NavAbout];
        foreach (var b in navs)
        {
            bool on = Equals(b.Tag, page);
            b.Background = on ? selectedBg : clear;
            b.FontWeight = on ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        }
        MountIcons();
        UpdateHistoryBadges();

        (PageTitleText.Text, PageSubtitleText.Text) = page switch
        {
            "general" => ("通用", "语言、启动行为与导入方式"),
            "appearance" => ("外观", "应用界面的外观与配色；书页纸色在「阅读」中设置"),
            "reading" => ("阅读", "新书的默认排版；单本书可在阅读页 Aa 中单独调整"),
            "foreign" => ("外文", "查词、难词注释、生词与离线翻译"),
            "listening" => ("朗读", "朗读、查词发音与播客音色"),
            "companion" => ("书伴", "阅读问答与播客撰稿所用的大模型"),
            "sync" => ("同步", "通过个人云盘在多台设备间同步"),
            "export" => ("笔记导出", "将划线与想法发送到常用笔记软件"),
            "storage" => ("存储", "书库、有声书与本地缓存占用的空间"),
            "data" => ("数据", "本地备份快照与阅读记录导出"),
            _ => ("关于", "版本、更新和作者"),
        };

        bool inspector = HasInspector(page);
        double maxInsp = Math.Max(252.0, Math.Min(480.0, (RootGrid.ActualWidth > 0 ? RootGrid.ActualWidth : 980.0) - 604.0));
        double inspW = Math.Clamp(_library.Prefs.SettingsInspectorWidth > 0 ? _library.Prefs.SettingsInspectorWidth : 304.0, 252.0, maxInsp);
        InspectorColumn.Width = inspector ? new GridLength(inspW) : new GridLength(0);
        InspectorBorder.Visibility = inspector ? Visibility.Visible : Visibility.Collapsed;
        InspectorResizeHandle.Visibility = inspector ? Visibility.Visible : Visibility.Collapsed;
        CenterCornerStack.Visibility = inspector ? Visibility.Collapsed : Visibility.Visible;

        UpdateMiddleHostVisibility(page);

        if (page != "appearance") RenderDynamicPage(page);
        if (inspector) RenderInspector(page);
        L10n.ApplyTree(RootGrid);
    }

    private void RenderDynamicPage(string page)
    {
        _liveVoiceProgressUpdater = null;
        _fontProgressUpdater = null;
        _alignerProgressUiUpdater = null;
        _senseVoiceProgressUiUpdater = null;
        DynamicPageHost.Children.Clear();
        VoiceSourceHost.Children.Clear();
        UpdateMiddleHostVisibility(page);
        switch (page)
        {
            case "general": BuildGeneralPage(); break;
            case "reading": BuildReadingPage(); break;
            case "foreign": BuildForeignPage(); break;
            case "listening": BuildListeningPage(); break;
            case "companion": BuildCompanionPage(); break;
            case "sync": BuildSyncPage(); break;
            case "export": BuildExportPage(); break;
            case "storage": BuildStoragePage(); break;
            case "data": BuildDataPage(); break;
            default: BuildAboutPage(); break;
        }
        L10n.ApplyTree(DynamicPageHost);
        if (VoiceSourceHost.Visibility == Visibility.Visible)
            L10n.ApplyTree(VoiceSourceHost);
    }

    // ---------- Shared Mac-style UI Builders ----------

    private bool IsDark => ThemeHelper.IsDark(RootGrid.ActualTheme);
    private YYPalette CurrentPalette => ThemeHelper.Palette(IsDark);
    private YYTintColors CurrentTint => ThemeHelper.TintColors(_library.Prefs.Tint, IsDark);

    private FrameworkElement MakeHeading(string text, string? rightNote = null)
    {
        var p = CurrentPalette;
        if (string.IsNullOrEmpty(rightNote))
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 11.5,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = ThemeHelper.Brush(p.T2),
                Margin = new Thickness(14, 6, 0, -4),
            };
        }
        var g = new Grid { ColumnSpacing = 12, Margin = new Thickness(14, 6, 14, -4) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.Children.Add(new TextBlock { Text = text, FontSize = 11.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T2), VerticalAlignment = VerticalAlignment.Center });
        var noteTb = new TextBlock { Text = rightNote, FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(noteTb, 1);
        g.Children.Add(noteTb);
        return g;
    }

    private TextBlock MakeNote(string text) => new()
    {
        Text = text,
        FontSize = 11,
        Foreground = ThemeHelper.Brush(CurrentPalette.T3),
        TextWrapping = TextWrapping.Wrap,
        LineHeight = 18,
        Margin = new Thickness(14, 0, 14, 0),
    };

    private Border MakeCard(params UIElement[] rows)
    {
        var p = CurrentPalette;
        var stack = new StackPanel { Spacing = 0 };
        for (int i = 0; i < rows.Length; i++)
        {
            if (i > 0) stack.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(16, 0, 16, 0) });
            stack.Children.Add(rows[i]);
        }
        return new Border
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            CornerRadius = new CornerRadius(12),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(IsDark ? Color.FromArgb(16, 255, 255, 255) : p.Line),
            BorderThickness = new Thickness(0.5),
            Child = stack,
        };
    }

    private Grid MakeRow(string title, string? subtitle, FrameworkElement? right = null, bool compact = false)
    {
        var p = CurrentPalette;
        var g = new Grid
        {
            ColumnSpacing = 16,
            Padding = new Thickness(16, compact ? 8 : 10, 16, compact ? 8 : 10),
            MinHeight = compact ? 38 : 46,
        };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new TextBlock { Text = title, FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1) });
        if (!string.IsNullOrEmpty(subtitle))
            left.Children.Add(new TextBlock { Text = subtitle, FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3), TextWrapping = TextWrapping.Wrap, LineHeight = 16 });
        g.Children.Add(left);

        if (right is not null)
        {
            if (right is DependencyObject rightObj && right is ToggleSwitch or ComboBox or Button or Slider)
            {
                if (string.IsNullOrEmpty(AutomationProperties.GetName(rightObj)))
                    AutomationProperties.SetName(rightObj, L10n.T(title));
            }
            right.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(right, 1);
            g.Children.Add(right);
        }
        return g;
    }

    private ToggleSwitch MakeSwitch(bool isOn, Action<bool> onChanged, bool enabled = true)
    {
        var sw = new ToggleSwitch { IsOn = isOn, IsEnabled = enabled };
        sw.Toggled += (_, _) => { if (sw.IsEnabled) onChanged(sw.IsOn); };
        return sw;
    }

    private Border MakeSegments<T>((T Value, string Label)[] items, T current, Action<T> onSelect, double width = 0) where T : notnull
    {
        var p = CurrentPalette;
        var grid = new Grid { ColumnSpacing = 2 };
        if (width > 0) grid.Width = width;
        for (int i = 0; i < items.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var (val, lbl) = items[i];
            bool on = EqualityComparer<T>.Default.Equals(val, current);
            var b = new Button
            {
                Content = lbl,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Height = 24,
                MinHeight = 24,
                Padding = new Thickness(width > 0 ? 4 : 8, 0, width > 0 ? 4 : 8, 0),
                FontSize = 11,
                FontWeight = on ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                Foreground = ThemeHelper.Brush(on ? p.T1 : p.T2),
                Background = on ? ThemeHelper.Brush(p.Card) : new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(7),
                UseSystemFocusVisuals = false,
            };
            T captured = val;
            b.Click += (_, _) => onSelect(captured);
            Grid.SetColumn(b, i);
            grid.Children.Add(b);
        }
        return new Border
        {
            Background = ThemeHelper.Brush(p.Inset),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(2.5),
            Child = grid,
        };
    }

    private Grid MakeInlineInputRow(string label, string value, string placeholder, Action<string> onChanged, bool isPassword = false)
    {
        var p = CurrentPalette;
        var g = new Grid { ColumnSpacing = 12, Padding = new Thickness(16, 8, 16, 8), MinHeight = 42 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        g.Children.Add(new TextBlock { Text = label, FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1), VerticalAlignment = VerticalAlignment.Center });

        if (isPassword)
        {
            var pb = new PasswordBox
            {
                Password = value,
                PlaceholderText = placeholder,
                BorderThickness = new Thickness(1),
                BorderBrush = ThemeHelper.Brush(p.Line),
                Background = ThemeHelper.Brush(p.Bg),
                CornerRadius = new CornerRadius(7),
                FontSize = 12,
            };
            pb.PasswordChanged += (_, _) => onChanged(pb.Password);
            Grid.SetColumn(pb, 1);
            g.Children.Add(pb);
        }
        else
        {
            var tb = new TextBox
            {
                Text = value,
                PlaceholderText = placeholder,
                BorderThickness = new Thickness(1),
                BorderBrush = ThemeHelper.Brush(p.Line),
                Background = ThemeHelper.Brush(p.Bg),
                CornerRadius = new CornerRadius(7),
                FontSize = 12,
            };
            tb.TextChanged += (_, _) => onChanged(tb.Text);
            Grid.SetColumn(tb, 1);
            g.Children.Add(tb);
        }
        return g;
    }

    private Border MakeStatusPill(string text, Color fg, Color? bg = null) => new()
    {
        CornerRadius = new CornerRadius(9),
        Height = 18,
        Padding = new Thickness(7, 0, 7, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Background = ThemeHelper.Brush(bg ?? Color.FromArgb(34, fg.R, fg.G, fg.B)),
        Child = new TextBlock { Text = text, FontSize = 10.5, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(fg), VerticalAlignment = VerticalAlignment.Center },
    };

    private sealed class FlowWrapPanel : Panel
    {
        public double HorizontalSpacing { get; set; } = 4.5;
        public double VerticalSpacing { get; set; } = 6.0;

        protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size availableSize)
        {
            double maxWidth = double.IsInfinity(availableSize.Width) || availableSize.Width <= 0 ? 10000 : availableSize.Width;
            double x = 0, y = 0, rowHeight = 0, usedWidth = 0;
            foreach (var child in Children)
            {
                child.Measure(new Windows.Foundation.Size(maxWidth, double.PositiveInfinity));
                var sz = child.DesiredSize;
                if (x > 0 && x + sz.Width > maxWidth)
                {
                    usedWidth = Math.Max(usedWidth, x - HorizontalSpacing);
                    x = 0;
                    y += rowHeight + VerticalSpacing;
                    rowHeight = 0;
                }
                x += sz.Width + HorizontalSpacing;
                rowHeight = Math.Max(rowHeight, sz.Height);
            }
            if (x > 0) usedWidth = Math.Max(usedWidth, x - HorizontalSpacing);
            return new Windows.Foundation.Size(Math.Min(maxWidth, usedWidth), y + rowHeight);
        }

        protected override Windows.Foundation.Size ArrangeOverride(Windows.Foundation.Size finalSize)
        {
            double maxWidth = finalSize.Width > 0 ? finalSize.Width : 10000;
            double x = 0, y = 0, rowHeight = 0;
            var rowItems = new List<(UIElement El, double X, Windows.Foundation.Size Sz)>();

            void FlushRow()
            {
                foreach (var (el, ix, sz) in rowItems)
                {
                    double top = y + (rowHeight - sz.Height);
                    el.Arrange(new Windows.Foundation.Rect(ix, top, sz.Width, sz.Height));
                }
                rowItems.Clear();
            }

            foreach (var child in Children)
            {
                var sz = child.DesiredSize;
                if (x > 0 && x + sz.Width > maxWidth)
                {
                    FlushRow();
                    x = 0;
                    y += rowHeight + VerticalSpacing;
                    rowHeight = 0;
                }
                rowItems.Add((child, x, sz));
                x += sz.Width + HorizontalSpacing;
                rowHeight = Math.Max(rowHeight, sz.Height);
            }
            FlushRow();
            return new Windows.Foundation.Size(finalSize.Width, y + rowHeight);
        }
    }

    // ---------- 右栏 SettingsInspector（1:1 MacSettingsSide.swift） ----------

    private void RenderInspector(string page)
    {
        _inspectorVoiceProgressUpdater = null;
        InspectorHost.Children.Clear();
        var p = CurrentPalette;
        var tint = CurrentTint;

        Grid InspectorHead(string title, string trailing)
        {
            var g = new Grid();
            g.Children.Add(new TextBlock { Text = title, FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
            g.Children.Add(new TextBlock { Text = trailing, FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom });
            return g;
        }

        switch (page)
        {
            case "reading":
            {
                var rs = _library.GetReaderSettings();
                var (paperName, bgCol, inkCol, isDarkPaper) = ResolvePaperColors(rs);
                string fontName = DescribeReaderFontName(rs);
                InspectorHost.Children.Add(InspectorHead("样张", $"{paperName} · {fontName} · {(int)Math.Round(rs.FontSize)}"));

                string baseFallbackFam = (rs.Font switch
                {
                    Font.Sans => YYFontFamily.Sans,
                    Font.Kai => YYFontFamily.Kaiti,
                    Font.WenKai => YYFontFamily.Wenkai,
                    _ => YYFontFamily.Serif,
                }).WindowsFamily();
                string fam = !string.IsNullOrEmpty(rs.FontFace)
                    ? _library.Fonts.ResolveXamlFontSource(
                        rs.FontFace,
                        rs.FontFace == "notosans" ? YYFontFamily.Sans.WindowsFamily() : baseFallbackFam)
                    : (rs.Font == Font.WenKai
                        ? _library.Fonts.ResolveXamlFontSource("wenkai", YYFontFamily.Wenkai.WindowsFamily())
                        : baseFallbackFam);
                double samplePt = Math.Round(rs.FontSize * 0.68);
                double lh = (rs.LineHeight ?? 1.85) * samplePt;
                string indent = (rs.Indent ?? true) ? "　　" : "";

                var sampleStack = new StackPanel { Spacing = 8 };
                var headLine = new Grid();
                headLine.Children.Add(new TextBlock { Text = "故乡", FontSize = 9.5, Foreground = ThemeHelper.Brush(inkCol), Opacity = 0.45 });
                headLine.Children.Add(new TextBlock { Text = "12 / 31", FontSize = 9.5, Foreground = ThemeHelper.Brush(inkCol), Opacity = 0.45, HorizontalAlignment = HorizontalAlignment.Right });
                sampleStack.Children.Add(headLine);

                foreach (var para in new[]
                {
                    "我冒了严寒，回到相隔二千余里，别了二十余年的故乡去。",
                    "时候既然是深冬；渐近故乡时，天气又阴晦了，冷风吹进船舱中，呜呜的响，从篷隙向外一望，苍黄的天底下，远近横着几个萧索的荒村，没有一些活气。",
                })
                {
                    sampleStack.Children.Add(new TextBlock
                    {
                        Text = indent + para,
                        FontFamily = new FontFamily(fam),
                        FontSize = samplePt,
                        LineHeight = lh,
                        TextAlignment = (rs.Justify ?? true) ? TextAlignment.Justify : TextAlignment.Left,
                        Foreground = ThemeHelper.Brush(inkCol),
                        TextWrapping = TextWrapping.Wrap,
                    });
                }

                InspectorHost.Children.Add(new Border
                {
                    MinHeight = 228,
                    CornerRadius = new CornerRadius(9),
                    Background = ThemeHelper.Brush(bgCol),
                    BorderBrush = ThemeHelper.Brush(isDarkPaper ? Colors.Transparent : p.Line),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(14, 12, 14, 12),
                    Child = sampleStack,
                });
                InspectorHost.Children.Add(new TextBlock { Text = "节选自鲁迅《故乡》", FontSize = 11, Foreground = ThemeHelper.Brush(p.T3) });
                break;
            }

            case "foreign":
            {
                var rs = _library.GetReaderSettings();
                var (_, bgCol, inkCol, isDarkPaper) = ResolvePaperColors(rs);
                var ink2Col = Color.FromArgb(165, inkCol.R, inkCol.G, inkCol.B);
                bool glossEnabled = _library.Prefs.ForeignGlossEnabled && _library.Prefs.GlossLevel > 0;
                string lvlText = !glossEnabled
                    ? "不写释义"
                    : _library.Prefs.GlossVocabOnly
                        ? "只写生词本里的"
                        : _library.Prefs.GlossLevel switch { 2 => "四级及以上", 3 => "六级及以上", 4 => "考研及以上", 5 => "雅思及以上", _ => "GRE 及以上" };
                InspectorHost.Children.Add(InspectorHead("样张", lvlText));

                // 1:1 MacSettingsSide.swift ForeignSample: individual words in FlowWrapPanel so narrowing inspector wraps naturally without horizontal clipping
                (string Word, string? Gloss, byte Level, bool InVocab)[] sampleTokens =
                [
                    ("It", null, 0, false), ("was", null, 0, false), ("the", null, 0, false), ("best", null, 0, false),
                    ("of", null, 0, false), ("times,", null, 0, false), ("it", null, 0, false), ("was", null, 0, false),
                    ("the", null, 0, false), ("worst", null, 0, false), ("of", null, 0, false), ("times,", null, 0, false),
                    ("it", null, 0, false), ("was", null, 0, false), ("the", null, 0, false), ("age", null, 0, false),
                    ("of", null, 0, false), ("wisdom,", "智慧", 2, false), ("it", null, 0, false), ("was", null, 0, false),
                    ("the", null, 0, false), ("age", null, 0, false), ("of", null, 0, false), ("foolishness,", "愚蠢", 4, false),
                    ("it", null, 0, false), ("was", null, 0, false), ("the", null, 0, false), ("epoch", "时代", 5, false),
                    ("of", null, 0, false), ("belief,", "信仰", 3, false), ("it", null, 0, false), ("was", null, 0, false),
                    ("the", null, 0, false), ("epoch", null, 0, false), ("of", null, 0, false), ("incredulity…", "怀疑", 6, true),
                ];

                bool markVocab = _library.Prefs.ForeignMarkVocab;
                var wrapPanel = new FlowWrapPanel { HorizontalSpacing = 4.5, VerticalSpacing = 6.0 };
                var latinFont = new FontFamily(YYFontFamily.LatinSerif.WindowsFamily());

                foreach (var w in sampleTokens)
                {
                    bool showGloss = glossEnabled
                        && w.Gloss is not null
                        && (_library.Prefs.GlossVocabOnly ? w.InVocab : w.Level >= _library.Prefs.GlossLevel);

                    var token = new StackPanel { Spacing = 0 };
                    if (showGloss)
                    {
                        token.Children.Add(new TextBlock
                        {
                            Text = w.Gloss!,
                            FontSize = 9,
                            Foreground = ThemeHelper.Brush(ink2Col),
                            Margin = new Thickness(0, 0, 0, 1),
                        });
                    }
                    token.Children.Add(new TextBlock
                    {
                        Text = w.Word,
                        FontFamily = latinFont,
                        FontSize = 12,
                        Foreground = ThemeHelper.Brush(inkCol),
                    });
                    if (markVocab && w.InVocab)
                    {
                        var dashRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(0, 1, 0, 0) };
                        for (int d = 0; d < 12; d++)
                            dashRow.Children.Add(new Border { Width = 3, Height = 1, Background = ThemeHelper.Brush(inkCol), Opacity = 0.62 });
                        token.Children.Add(dashRow);
                    }
                    else if (markVocab)
                    {
                        token.Children.Add(new Border { Height = 2 });
                    }
                    wrapPanel.Children.Add(token);
                }

                InspectorHost.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(9),
                    Background = ThemeHelper.Brush(bgCol),
                    BorderBrush = ThemeHelper.Brush(isDarkPaper ? Colors.Transparent : p.Line),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(14),
                    Child = wrapPanel,
                });
                string cap = (!glossEnabled ? "难词上方不写释义；" : "") + (markVocab ? "虚线是生词本里的词" : "生词不标");
                InspectorHost.Children.Add(new TextBlock { Text = cap, FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), TextWrapping = TextWrapping.Wrap });
                break;
            }

            case "listening":
            {
                BuildListeningInspector(p, tint);
                break;
            }

            case "companion":
            {
                BuildCompanionInspector(p, tint);
                break;
            }

            case "export":
            {
                BuildExportInspector(p, tint);
                break;
            }

            case "storage":
            {
                InspectorHost.Children.Add(InspectorHead("书库", ""));
                var cardStack = new StackPanel { Spacing = 5 };
                cardStack.Children.Add(new TextBlock { Text = Path.GetFileName(_library.DataDir), FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1) });
                cardStack.Children.Add(new TextBlock
                {
                    Text = _library.DataDir,
                    FontFamily = new FontFamily(YYFontFamily.Mono.WindowsFamily()),
                    FontSize = 10.5,
                    Foreground = ThemeHelper.Brush(p.T3),
                    TextWrapping = TextWrapping.Wrap,
                });
                var openSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
                openSp.Children.Add(YYIcons.Make("folder", 12.5, ThemeHelper.Brush(p.T1)));
                openSp.Children.Add(new TextBlock { Text = "在资源管理器中显示", FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1) });
                var openBtn = new Button
                {
                    Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
                    Padding = new Thickness(0, 4, 6, 2),
                    Margin = new Thickness(0, 4, 0, 0),
                    Content = openSp,
                };
                openBtn.Click += (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_library.DataDir}\"") { UseShellExecute = true });
                cardStack.Children.Add(openBtn);

                InspectorHost.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(11),
                    Background = ThemeHelper.Brush(p.Card),
                    BorderBrush = ThemeHelper.Brush(p.Line),
                    BorderThickness = new Thickness(0.5),
                    Padding = new Thickness(14, 12, 14, 12),
                    Child = cardStack,
                });
                break;
            }

            case "data":
            {
                // 1:1 MacSettingsSide.swift:169, 220-251：异步读取 PeekSnapshot 元数据（时间、设备名、含书文件、体积）
                InspectorHost.Children.Add(InspectorHead("上次快照", ""));
                string snapDir = Path.Combine(_library.DataDir, "snapshots");
                Directory.CreateDirectory(snapDir);

                var headlineTb = new TextBlock { Text = "正在读取…", FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T2) };
                var detailTb = new TextBlock { Text = "", FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };

                var cardStack = new StackPanel { Spacing = 5 };
                cardStack.Children.Add(headlineTb);
                cardStack.Children.Add(detailTb);

                var openSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
                openSp.Children.Add(YYIcons.Make("folder", 12.5, ThemeHelper.Brush(p.T1)));
                openSp.Children.Add(new TextBlock { Text = "在资源管理器中显示", FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1) });
                var openBtn = new Button
                {
                    Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
                    Padding = new Thickness(0, 4, 6, 2),
                    Margin = new Thickness(0, 4, 0, 0),
                    Content = openSp,
                };
                openBtn.Click += (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{snapDir}\"") { UseShellExecute = true });
                cardStack.Children.Add(openBtn);

                InspectorHost.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(11),
                    Background = ThemeHelper.Brush(p.Card),
                    BorderBrush = ThemeHelper.Brush(p.Line),
                    BorderThickness = new Thickness(0.5),
                    Padding = new Thickness(14, 12, 14, 12),
                    Child = cardStack,
                });

                _ = Task.Run(() =>
                {
                    var files = Directory.GetFiles(snapDir, "*.yuyue")
                        .Select(f =>
                        {
                            var fi = new FileInfo(f);
                            SnapshotAbout? about = null;
                            try { about = _library.Core.PeekSnapshot(fi.FullName); } catch { }
                            long sortMs = about?.MadeAt > 0
                                ? about.MadeAt
                                : new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds();
                            return (File: fi, About: about, SortMs: sortMs);
                        })
                        .OrderByDescending(x => x.SortMs)
                        .ToArray();
                    return files.Length > 0 ? files[0] : ((FileInfo File, SnapshotAbout? About, long SortMs)?)null;
                }).ContinueWith(t =>
                {
                    if (t.Status != TaskStatus.RanToCompletion) return;
                    var latest = t.Result;
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (_closed || _activePage != "data") return;
                        var pal = CurrentPalette;
                        if (latest is { } item)
                        {
                            var madeLocal = item.About is not null && item.About.MadeAt > 0
                                ? DateTimeOffset.FromUnixTimeMilliseconds(item.About.MadeAt).LocalDateTime
                                : item.File.LastWriteTime;
                            headlineTb.Text = madeLocal.Year == DateTime.Now.Year
                                ? $"{madeLocal.Month} 月 {madeLocal.Day} 日 {madeLocal:HH:mm}"
                                : $"{madeLocal.Year} 年 {madeLocal.Month} 月 {madeLocal.Day} 日 {madeLocal:HH:mm}";
                            headlineTb.FontSize = 16.5;
                            headlineTb.Foreground = ThemeHelper.Brush(pal.T1);

                            string dev = !string.IsNullOrWhiteSpace(item.About?.DeviceName) ? item.About!.DeviceName : Environment.MachineName;
                            long bytes = item.About is not null && item.About.Bytes > 0 ? (long)item.About.Bytes : item.File.Length;
                            string filesTag = item.About?.Parts.Files == true ? " · 含书文件" : "";
                            detailTb.Text = L10n.T($"{dev}{filesTag} · {FormatBytes(bytes)}");
                            detailTb.Visibility = Visibility.Visible;
                        }
                        else
                        {
                            headlineTb.Text = L10n.T("暂无快照");
                            headlineTb.FontSize = 14.5;
                            headlineTb.Foreground = ThemeHelper.Brush(pal.T2);
                            detailTb.Text = L10n.T("勾选需要备份的内容后，点击「生成快照」");
                            detailTb.Visibility = Visibility.Visible;
                        }
                    });
                });
                break;
            }
        }
        L10n.ApplyTree(InspectorHost);
    }

    private string DescribeReaderFontName(ReaderSettings rs) =>
        !string.IsNullOrEmpty(rs.FontFace)
            ? rs.FontFace switch
            {
                "serif" => "宋体",
                "kai" => "楷体",
                "system" => "系统",
                "notoserif" => "思源宋体",
                "notosans" => "思源黑体",
                "wenkai" => "霞鹜文楷",
                _ when rs.FontFace.StartsWith("file:", StringComparison.OrdinalIgnoreCase) => _library.Fonts.GetFontFileDisplayName(rs.FontFace[5..]),
                _ when rs.FontFace.StartsWith("local:", StringComparison.OrdinalIgnoreCase) => FontShelfService.CleanRegistryFontFamilyName(rs.FontFace[6..]),
                _ when rs.FontFace.StartsWith("sys:", StringComparison.OrdinalIgnoreCase) => FontShelfService.CleanRegistryFontFamilyName(rs.FontFace[4..]),
                _ => _library.Fonts.Catalog.FirstOrDefault(c => c.Id == rs.FontFace)?.Name ?? rs.FontFace,
            }
            : rs.Font switch { Font.Serif => "宋体", Font.Kai => "楷体", Font.WenKai => "霞鹜文楷", Font.Sans => "黑体", _ => "系统" };

    private (string Name, Color Bg, Color Ink, bool Dark) ResolvePaperColors(ReaderSettings rs)
    {
        var resolved = PaperAppearance.Resolve(_library, rs);
        if (resolved.Custom is CustomPaper cp)
        {
            try
            {
                var pc = YuyueCoreMethods.PaperColors(cp);
                return (cp.Name, CustomPaperDialog.ParseHexColor(pc.Bg, Colors.Beige), CustomPaperDialog.ParseHexColor(pc.Ink, Colors.Black), pc.Dark);
            }
            catch { }
        }
        var tok = resolved.Preset switch
        {
            Paper.Dark => YYPapers.Dark,
            Paper.Kami => YYPapers.Kami,
            Paper.KamiNight => YYPapers.Kaminight,
            Paper.Sage => YYPapers.Sage,
            Paper.Eink => YYPapers.Eink,
            _ => YYPapers.Plain,
        };
        string swiftName = resolved.Preset switch
        {
            Paper.Dark => "暗黑",
            Paper.Kami => "纸本",
            Paper.KamiNight => "纸本 · 夜",
            Paper.Sage => "芦苇",
            Paper.Eink => "铅纸",
            _ => "素白",
        };
        return (swiftName, tok.Bg, tok.Ink, tok.IsDark);
    }

    // ---------- 1. 通用（1:1 MacSettings.swift:599-691 & AppLanguage.swift:364-550） ----------

    private Border MakeLanguageSealBadge(string langKey, bool active)
    {
        var p = CurrentPalette;
        var tint = CurrentTint;
        Color bg = active ? tint.Soft : (IsDark ? Color.FromArgb(22, 255, 255, 255) : Color.FromArgb(14, 42, 43, 46));
        Color fg = active ? tint.Accent : p.T2;

        UIElement glyph = langKey switch
        {
            "zh-Hans" => new TextBlock
            {
                Text = "文",
                FontFamily = new FontFamily(YYFontFamily.Serif.WindowsFamily()),
                FontSize = 10.5,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = ThemeHelper.Brush(fg),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
            "en" => new TextBlock
            {
                Text = "Aa",
                FontFamily = new FontFamily(YYFontFamily.LatinSerif.WindowsFamily()),
                FontStyle = Windows.UI.Text.FontStyle.Italic,
                FontSize = 10.5,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = ThemeHelper.Brush(fg),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
            _ => YYIcons.Make("laptop", 11, ThemeHelper.Brush(fg)),
        };

        return new Border
        {
            Width = 20,
            Height = 20,
            CornerRadius = new CornerRadius(5.5),
            Background = ThemeHelper.Brush(bg),
            BorderBrush = ThemeHelper.Brush(active ? tint.Edge : Colors.Transparent),
            BorderThickness = new Thickness(active ? 0.75 : 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = glyph,
        };
    }

    private void BuildGeneralPage()
    {
        var p = CurrentPalette;
        var tint = CurrentTint;

        string curLang = string.IsNullOrWhiteSpace(_library.Prefs.UiLanguage) ? "system" : _library.Prefs.UiLanguage;
        string osLangName = System.Globalization.CultureInfo.CurrentUICulture.Name.StartsWith("en", StringComparison.OrdinalIgnoreCase)
            ? "English"
            : "中文";
        (string langLabel, string? langBadge, string subText) = curLang switch
        {
            "zh-Hans" => ("中文", null, "当前：中文 · 切换需确认重开，并回到当前页"),
            "en" => ("English", null, "当前：English · 切换需确认重开，并回到当前页"),
            _ => ("跟随系统", osLangName, $"当前：{osLangName} · 切换需确认重开，并回到当前页"),
        };

        Flyout? langFlyout = null;
        var menuStack = new StackPanel
        {
            Width = 224,
            Spacing = 2,
            Padding = new Thickness(6),
            Background = ThemeHelper.Brush(p.Card),
        };

        async Task PickLanguageAsync(string key, string displayTitle)
        {
            langFlyout?.Hide();
            string prevLang = _library.Prefs.UiLanguage;
            string normPrev = string.IsNullOrWhiteSpace(prevLang) ? "system" : prevLang;
            if (key == normPrev) return;

            bool audioPlaying = _library.SystemIntegration.IsAudioPlaying;
            string confirmTitle = L10n.T($"切换为「{displayTitle}」？");
            string confirmBody = L10n.T(audioPlaying
                ? "正在朗读音频，将在下次启动时生效。"
                : "确认后将立即重开应用并回到当前设置页。");
            string confirmBtnText = L10n.T(audioPlaying ? "好" : "立即重开");
            bool ok = await YYChrome.ShowConfirmAsync(
                RootGrid.XamlRoot,
                IsDark,
                confirmTitle,
                confirmBody,
                confirmText: confirmBtnText,
                cancelText: L10n.T("取消"),
                tint: _library.Prefs.Tint);
            if (!ok) return;

            string prevDisplay = normPrev switch { "zh-Hans" => "中文", "en" => "English", _ => "跟随系统" };
            _library.Prefs.UiLanguage = key;
            _library.SavePrefs();
            _library.ApplyUiLanguage();
            RecordPrefChange(
                nameof(DevicePrefs.UiLanguage),
                "语言",
                displayTitle,
                prevDisplay,
                prevLang,
                key,
                undo: () =>
                {
                    _library.Prefs.UiLanguage = prevLang;
                    _library.SavePrefs();
                    _library.ApplyUiLanguage();
                    App.CurrentApp.BroadcastAppearanceChanged();
                },
                redo: () =>
                {
                    _library.Prefs.UiLanguage = key;
                    _library.SavePrefs();
                    _library.ApplyUiLanguage();
                    App.CurrentApp.BroadcastAppearanceChanged();
                });

            if (!audioPlaying)
            {
                SystemIntegrationService.RelaunchApplication("--settings general");
                return;
            }
            App.CurrentApp.BroadcastAppearanceChanged();
            RenderDynamicPage("general");
        }

        void AddLanguageMenuItem(string key, string title, string subtitle)
        {
            bool active = curLang == key;
            var rowGrid = new Grid { ColumnSpacing = 9, Padding = new Thickness(8, 6, 8, 6) };
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            rowGrid.Children.Add(MakeLanguageSealBadge(key, active));

            var mid = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            mid.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 12.5,
                FontWeight = active ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Medium,
                Foreground = ThemeHelper.Brush(p.T1),
            });
            mid.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontSize = 10.5,
                Foreground = ThemeHelper.Brush(p.T3),
            });
            Grid.SetColumn(mid, 1);
            rowGrid.Children.Add(mid);

            if (active)
            {
                var chk = YYIcons.Make("checkmark", 12, ThemeHelper.Brush(tint.Accent));
                chk.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(chk, 2);
                rowGrid.Children.Add(chk);
            }

            var itemBtn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(0),
                Background = active ? ThemeHelper.Brush(Color.FromArgb(14, p.T1.R, p.T1.G, p.T1.B)) : new SolidColorBrush(Colors.Transparent),
                UseSystemFocusVisuals = false,
                Content = rowGrid,
            };
            string capKey = key;
            string capTitle = title;
            itemBtn.Click += async (_, _) => await PickLanguageAsync(capKey, capTitle);
            menuStack.Children.Add(itemBtn);
        }

        AddLanguageMenuItem("zh-Hans", "中文", "简体中文界面");
        AddLanguageMenuItem("en", "English", "English interface");
        menuStack.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(8, 3, 8, 3) });
        AddLanguageMenuItem("system", L10n.T("跟随系统"), L10n.T($"当前：{osLangName}"));

        langFlyout = new Flyout
        {
            FlyoutPresenterStyle = (Style)Application.Current.Resources["YYFloatFlyoutPresenterStyle"],
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight,
            Content = menuStack,
        };

        var triggerGrid = new Grid { MinWidth = 160, ColumnSpacing = 8 };
        triggerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        triggerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        triggerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        triggerGrid.Children.Add(MakeLanguageSealBadge(curLang, active: false));

        var labelRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        labelRow.Children.Add(new TextBlock { Text = langLabel, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1), VerticalAlignment = VerticalAlignment.Center });
        if (!string.IsNullOrEmpty(langBadge))
        {
            labelRow.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(5),
                Background = ThemeHelper.Brush(IsDark ? Color.FromArgb(24, 255, 255, 255) : Color.FromArgb(14, 42, 43, 46)),
                Padding = new Thickness(6, 1.5, 6, 1.5),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = langBadge, FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T2) },
            });
        }
        Grid.SetColumn(labelRow, 1);
        triggerGrid.Children.Add(labelRow);
        var chev = YYIcons.Make("updown", 10, ThemeHelper.Brush(p.T3));
        Grid.SetColumn(chev, 2);
        triggerGrid.Children.Add(chev);

        var langBtn = new Button
        {
            Height = 32,
            Padding = new Thickness(8, 0, 10, 0),
            CornerRadius = new CornerRadius(9),
            Background = ThemeHelper.Brush(p.Inset),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0.5),
            UseSystemFocusVisuals = false,
            Content = triggerGrid,
            Flyout = langFlyout,
        };

        var reopenSw = MakeSwitch(_library.Prefs.ReopenLast, on =>
        {
            bool prev = !on;
            _library.Prefs.ReopenLast = on;
            _library.SavePrefs();
            RecordPrefChange(
                nameof(DevicePrefs.ReopenLast),
                "启动时继续上次阅读",
                on ? "开" : "关",
                prev ? "开" : "关",
                prev,
                on,
                undo: () => { _library.Prefs.ReopenLast = prev; _library.SavePrefs(); },
                redo: () => { _library.Prefs.ReopenLast = on; _library.SavePrefs(); });
        });

        var loginSw = MakeSwitch(_library.Prefs.AutoLaunchOnLogin, on =>
        {
            bool prev = !on;
            _library.Prefs.AutoLaunchOnLogin = on;
            _library.SavePrefs();
            SystemIntegrationService.SyncLoginStartup(on);
            _library.SystemIntegration.EnsureTrayVisible();
            RecordPrefChange(
                nameof(DevicePrefs.AutoLaunchOnLogin),
                "登录时后台启动",
                on ? "开" : "关",
                prev ? "开" : "关",
                prev,
                on,
                undo: () =>
                {
                    _library.Prefs.AutoLaunchOnLogin = prev;
                    _library.SavePrefs();
                    SystemIntegrationService.SyncLoginStartup(prev);
                    _library.SystemIntegration.EnsureTrayVisible();
                },
                redo: () =>
                {
                    _library.Prefs.AutoLaunchOnLogin = on;
                    _library.SavePrefs();
                    SystemIntegrationService.SyncLoginStartup(on);
                    _library.SystemIntegration.EnsureTrayVisible();
                });
        });

        var awakeSw = MakeSwitch(_library.Prefs.KeepAwakeWhileListening, on =>
        {
            bool prev = !on;
            _library.Prefs.KeepAwakeWhileListening = on;
            _library.SavePrefs();
            _library.SystemIntegration.ApplyPowerStayAwake();
            RecordPrefChange(
                nameof(DevicePrefs.KeepAwakeWhileListening),
                "朗读时防止休眠",
                on ? "开" : "关",
                prev ? "开" : "关",
                prev,
                on,
                undo: () =>
                {
                    _library.Prefs.KeepAwakeWhileListening = prev;
                    _library.SavePrefs();
                    _library.SystemIntegration.ApplyPowerStayAwake();
                },
                redo: () =>
                {
                    _library.Prefs.KeepAwakeWhileListening = on;
                    _library.SavePrefs();
                    _library.SystemIntegration.ApplyPowerStayAwake();
                });
        });

        DynamicPageHost.Children.Add(MakeCard(
            MakeRow("语言", subText, langBtn),
            MakeRow("启动时继续上次阅读", "关闭则进入书架", reopenSw),
            MakeRow("登录时后台启动", "常驻托盘，不弹出窗口，按时生成播客", loginSw),
            MakeRow("朗读时防止系统休眠", "合上屏幕仍会暂停朗读", awakeSw)));

        // 1:1 MacSettings.swift:770-776：导入卡片仅保留拖入书架行
        DynamicPageHost.Children.Add(MakeHeading("导入"));
        var fmtBadge = new TextBlock
        {
            Text = "EPUB PDF MOBI AZW3 TXT",
            FontFamily = new FontFamily(YYFontFamily.Mono.WindowsFamily()),
            FontSize = 11,
            Foreground = ThemeHelper.Brush(p.T3),
        };
        DynamicPageHost.Children.Add(MakeCard(
            MakeRow("拖入书架即可导入", "文件会复制到书库，移动或删除原文件不受影响", fmtBadge)));
    }

    // ---------- 2. 阅读（1:1 MacSettings.swift & MacFonts.swift:177-483） ----------

    private void BuildReadingPage()
    {
        var rs = _library.GetReaderSettings();
        var p = CurrentPalette;
        var tint = CurrentTint;

        string DescribeOldValue(string label, ReaderSettings oldRs) => label switch
        {
            "纸色" => ResolvePaperColors(oldRs).Name,
            "字体" => DescribeReaderFontName(oldRs),
            "字号" => $"{(int)Math.Round(oldRs.FontSize)}",
            "行距" => oldRs.LineHeight is <= 1.7 ? "紧" : oldRs.LineHeight is >= 2.05 ? "松" : "适中",
            "翻页方式" => oldRs.Mode == LayoutMode.Scrolled ? "上下滚动" : "左右翻页",
            "两端对齐" => (oldRs.Justify ?? true) ? "开" : "关",
            "首行缩进" => (oldRs.Indent ?? true) ? "开" : "关",
            "标点挤压" => oldRs.Compress ? "开" : "关",
            _ => "",
        };

        void Update(string label, string valText, Func<ReaderSettings, ReaderSettings> edit)
        {
            var prev = rs;
            string oldVal = DescribeOldValue(label, prev);
            var next = edit(rs);
            rs = next;
            _library.SaveReaderSettings(next);
            App.CurrentApp.BroadcastReaderSettingsChanged();
            RecordChange(
                label,
                valText,
                undo: () => { _library.SaveReaderSettings(prev); App.CurrentApp.BroadcastReaderSettingsChanged(); },
                redo: () => { _library.SaveReaderSettings(next); App.CurrentApp.BroadcastReaderSettingsChanged(); },
                oldValue: string.IsNullOrEmpty(oldVal) || oldVal == valText ? null : oldVal,
                prefKey: "reader:settings",
                oldRawJson: System.Text.Json.JsonSerializer.Serialize(prev),
                newRawJson: System.Text.Json.JsonSerializer.Serialize(next),
                pageOverride: "reading");
            RenderInspector("reading");
        }

        // 1. 纸张磁贴网格（左对齐双横线墨水样条 + 右键重命名/删除确认）
        var resolvedPaper = PaperAppearance.Resolve(_library, rs);
        var paperWrap = new StackPanel { Spacing = 10, Padding = new Thickness(16, 14, 16, 14) };
        var paperHeader = new Grid();
        paperHeader.Children.Add(new TextBlock { Text = "纸色", FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1), VerticalAlignment = VerticalAlignment.Center });
        var followBox = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        followBox.Children.Add(new TextBlock { Text = "跟随外观", FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T2), VerticalAlignment = VerticalAlignment.Center });
        var followSwitch = MakeSwitch(_library.Prefs.PaperFollowAppearance, v =>
        {
            bool prevFollow = _library.Prefs.PaperFollowAppearance;
            var prevRs = rs;
            bool save = PaperAppearance.SetFollow(_library, v, rs, out var updated);
            var nextRs = save ? updated : rs;
            if (save)
            {
                rs = updated;
                _library.SaveReaderSettings(rs);
            }
            App.CurrentApp.BroadcastReaderSettingsChanged();
            RecordPrefChange(
                nameof(DevicePrefs.PaperFollowAppearance),
                "纸色跟随外观",
                v ? "开" : "关",
                prevFollow ? "开" : "关",
                prevFollow,
                v,
                undo: () =>
                {
                    _library.Prefs.PaperFollowAppearance = prevFollow;
                    _library.SavePrefs();
                    if (save) _library.SaveReaderSettings(prevRs);
                    App.CurrentApp.BroadcastReaderSettingsChanged();
                },
                redo: () =>
                {
                    _library.Prefs.PaperFollowAppearance = v;
                    _library.SavePrefs();
                    if (save) _library.SaveReaderSettings(nextRs);
                    App.CurrentApp.BroadcastReaderSettingsChanged();
                });
            RenderInspector("reading");
            RenderDynamicPage("reading");
        });
        AutomationProperties.SetName(followSwitch, L10n.T("纸色跟随外观"));
        followBox.Children.Add(followSwitch);
        paperHeader.Children.Add(followBox);
        paperWrap.Children.Add(paperHeader);

        CustomPaper[] customList;
        try { customList = _library.Core.CustomPapers(); } catch { customList = []; }

        (Paper Kind, string Name, YYPaper Token)[] presets =
        [
            (Paper.Plain, "素白", YYPapers.Plain),
            (Paper.Dark, "暗黑", YYPapers.Dark),
            (Paper.Kami, "纸本", YYPapers.Kami),
            (Paper.KamiNight, "纸本 · 夜", YYPapers.Kaminight),
            (Paper.Sage, "芦苇", YYPapers.Sage),
            (Paper.Eink, "铅纸", YYPapers.Eink),
        ];

        int totalTiles = presets.Length + customList.Length + 1;
        int cols = 6;
        int rows = (totalTiles + cols - 1) / cols;
        var paperGrid = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
        for (int c = 0; c < cols; c++) paperGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int r = 0; r < rows; r++) paperGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Border MakePaperTileCard(Color bg, Color ink, Color border, string name, bool active)
        {
            var lines = new StackPanel
            {
                Spacing = 3.5,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(9, 0, 0, 0),
            };
            lines.Children.Add(new Border { Width = 24, Height = 2, CornerRadius = new CornerRadius(1), Background = ThemeHelper.Brush(ink), Opacity = 0.72, HorizontalAlignment = HorizontalAlignment.Left });
            lines.Children.Add(new Border { Width = 16, Height = 2, CornerRadius = new CornerRadius(1), Background = ThemeHelper.Brush(ink), Opacity = 0.45, HorizontalAlignment = HorizontalAlignment.Left });
            var swatch = new Border
            {
                Height = 30,
                CornerRadius = new CornerRadius(6),
                Background = ThemeHelper.Brush(bg),
                BorderBrush = ThemeHelper.Brush(border),
                BorderThickness = new Thickness(0.5),
                Child = lines,
            };
            var inner = new StackPanel { Spacing = 5 };
            inner.Children.Add(swatch);
            inner.Children.Add(new TextBlock
            {
                Text = name,
                FontSize = 10.5,
                FontWeight = active ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                Foreground = ThemeHelper.Brush(active ? p.T1 : p.T2),
                HorizontalAlignment = HorizontalAlignment.Center,
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            return new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(5, 6, 5, 6),
                Background = ThemeHelper.Brush(p.Card),
                BorderBrush = ThemeHelper.Brush(active ? tint.Accent : p.Line),
                BorderThickness = new Thickness(active ? 1.5 : 0.75),
                Child = inner,
            };
        }

        int cellIdx = 0;
        foreach (var (kind, name, tok) in presets)
        {
            bool on = resolvedPaper.Custom is null && resolvedPaper.Preset == kind;
            var btn = new Button
            {
                Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                UseSystemFocusVisuals = false,
                Content = MakePaperTileCard(tok.Bg, tok.Ink, tok.Line, name, on),
            };
            Paper pk = kind;
            string pName = name;
            btn.Click += (_, _) =>
            {
                bool save = PaperAppearance.PickPreset(_library, pk, rs, out var updated);
                if (save) Update("纸色", pName, _ => updated);
                else { App.CurrentApp.BroadcastReaderSettingsChanged(); RenderInspector("reading"); }
                RenderDynamicPage("reading");
            };
            Grid.SetRow(btn, cellIdx / cols);
            Grid.SetColumn(btn, cellIdx % cols);
            paperGrid.Children.Add(btn);
            cellIdx++;
        }

        foreach (var cp in customList)
        {
            var cpItem = cp;
            var pc = YuyueCoreMethods.PaperColors(cpItem);
            bool on = resolvedPaper.Custom?.Id == cpItem.Id;
            var tileCard = MakePaperTileCard(CustomPaperDialog.ParseHexColor(pc.Bg, Colors.Beige), CustomPaperDialog.ParseHexColor(pc.Ink, Colors.Black), p.Line, cpItem.Name, on);

            var ctx = new MenuFlyout();
            var editMi = new MenuFlyoutItem { Text = L10n.T("调整颜色…") };
            editMi.Click += async (_, _) =>
            {
                var edited = await CustomPaperDialog.ShowEditorAsync(RootGrid.XamlRoot, _library, cpItem);
                if (edited is not null)
                {
                    bool save = PaperAppearance.PickCustom(_library, edited, rs, out var updated);
                    if (save) Update("纸色", edited.Name, _ => updated);
                    else { App.CurrentApp.BroadcastReaderSettingsChanged(); RenderInspector("reading"); }
                }
                RenderDynamicPage("reading");
            };
            var renameMi = new MenuFlyoutItem { Text = L10n.T("重命名…") };
            renameMi.Click += async (_, _) =>
            {
                string? newName = await YYChrome.ShowPromptAsync(
                    RootGrid.XamlRoot,
                    IsDark,
                    L10n.T("重命名纸色"),
                    L10n.T("输入纸色名称"),
                    cpItem.Name,
                    confirmText: L10n.T("好"),
                    cancelText: L10n.T("取消"),
                    tint: _library.Prefs.Tint);
                if (string.IsNullOrWhiteSpace(newName) || newName == cpItem.Name) return;
                try
                {
                    var saved = _library.Core.SaveCustomPaper(cpItem with { Name = newName });
                    App.CurrentApp.BroadcastReaderSettingsChanged();
                    RenderInspector("reading");
                    RenderDynamicPage("reading");
                    ShowFlashBanner($"✓ 已重命名为「{saved.Name}」");
                }
                catch (Exception ex)
                {
                    await ShowInfoDialogAsync("重命名失败", ex.Message);
                }
            };
            var dupMi = new MenuFlyoutItem { Text = L10n.T("复制一张") };
            dupMi.Click += (_, _) =>
            {
                var copy = CustomPaperDialog.DuplicateCustomPaper(_library, cpItem);
                if (copy is not null)
                {
                    bool save = PaperAppearance.PickCustom(_library, copy, rs, out var updated);
                    if (save) Update("纸色", copy.Name, _ => updated);
                    else { App.CurrentApp.BroadcastReaderSettingsChanged(); RenderInspector("reading"); }
                }
                RenderDynamicPage("reading");
            };
            var delMi = new MenuFlyoutItem { Text = L10n.T("删除…"), Foreground = ThemeHelper.Brush(p.Danger) };
            delMi.Click += async (_, _) =>
            {
                bool confirmed = await YYChrome.ShowConfirmAsync(
                    RootGrid.XamlRoot,
                    IsDark,
                    L10n.T($"删除纸色「{cpItem.Name}」？"),
                    L10n.T("正在使用该纸色的书页会自动切回相近的预设纸色。"),
                    confirmText: L10n.T("删除"),
                    cancelText: L10n.T("取消"),
                    destructive: true,
                    tint: _library.Prefs.Tint);
                if (!confirmed) return;

                try { _library.Core.DeleteCustomPaper(cpItem.Id); } catch { }
                PaperAppearance.Forget(_library, cpItem.Id);
                if (rs.CustomPaper == cpItem.Id)
                    Update("纸色", "纸本", s => s with { Paper = Paper.Kami, CustomPaper = null });
                else
                {
                    App.CurrentApp.BroadcastReaderSettingsChanged();
                    RenderInspector("reading");
                }
                RenderDynamicPage("reading");
            };
            ctx.Items.Add(editMi);
            ctx.Items.Add(renameMi);
            ctx.Items.Add(dupMi);
            ctx.Items.Add(new MenuFlyoutSeparator());
            ctx.Items.Add(delMi);

            var btn = new Button { Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0), UseSystemFocusVisuals = false, Content = tileCard, ContextFlyout = ctx };
            btn.Click += (_, _) =>
            {
                bool save = PaperAppearance.PickCustom(_library, cpItem, rs, out var updated);
                if (save) Update("纸色", cpItem.Name, _ => updated);
                else { App.CurrentApp.BroadcastReaderSettingsChanged(); RenderInspector("reading"); }
                RenderDynamicPage("reading");
            };
            Grid.SetRow(btn, cellIdx / cols);
            Grid.SetColumn(btn, cellIdx % cols);
            paperGrid.Children.Add(btn);
            cellIdx++;
        }

        // + 新建自定义纸张磁贴
        var addPaperInner = new StackPanel { Spacing = 5 };
        addPaperInner.Children.Add(new Border
        {
            Height = 30,
            CornerRadius = new CornerRadius(6),
            Background = ThemeHelper.Brush(p.Inset),
            Child = YYIcons.Make("plus", 12, ThemeHelper.Brush(p.T2)),
        });
        addPaperInner.Children.Add(new TextBlock { Text = "新建", FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T2), HorizontalAlignment = HorizontalAlignment.Center });
        var addPaperCard = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(5, 6, 5, 6),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0.75),
            Child = addPaperInner,
        };
        var addPaperBtn = new Button { Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0), UseSystemFocusVisuals = false, Content = addPaperCard };
        addPaperBtn.Click += async (_, _) =>
        {
            var created = await CustomPaperDialog.ShowEditorAsync(RootGrid.XamlRoot, _library);
            if (created is not null)
            {
                bool save = PaperAppearance.PickCustom(_library, created, rs, out var updated);
                if (save) Update("纸色", created.Name, _ => updated);
                else { App.CurrentApp.BroadcastReaderSettingsChanged(); RenderInspector("reading"); }
                RenderDynamicPage("reading");
            }
        };
        Grid.SetRow(addPaperBtn, cellIdx / cols);
        Grid.SetColumn(addPaperBtn, cellIdx % cols);
        paperGrid.Children.Add(addPaperBtn);
        paperWrap.Children.Add(paperGrid);

        // 2. 字体「永」字磁贴网格 + 已固定字体右键「从格子里拿掉」 + 「更多字体」字样册 (FontBook)
        var fontWrap = new StackPanel { Spacing = 10, Padding = new Thickness(16, 14, 16, 14) };
        fontWrap.Children.Add(new TextBlock { Text = "字体", FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1) });

        var tileEntries = new List<(string Id, string BaseName, Font? Kind, string? Face, string Fam, bool BuiltIn)>
        {
            ("system", "系统", Font.Sans, null, YYFontFamily.Sans.WindowsFamily(), true),
            ("serif", "宋体", Font.Serif, null, YYFontFamily.Serif.WindowsFamily(), true),
            ("kai", "楷体", Font.Kai, null, YYFontFamily.Kaiti.WindowsFamily(), true),
            ("wenkai", "霞鹜文楷", Font.WenKai, null, _library.Fonts.ResolveXamlFontSource("wenkai", YYFontFamily.Wenkai.WindowsFamily()), true),
        };

        foreach (string pinId in _library.Prefs.PinnedFontIds.Take(4))
        {
            if (string.IsNullOrWhiteSpace(pinId) || tileEntries.Any(e => e.Id == pinId)) continue;
            if (pinId.StartsWith("file:", StringComparison.Ordinal))
            {
                string fn = pinId[5..];
                string disp = _library.Fonts.GetFontFileDisplayName(fn);
                string fam = _library.Fonts.ResolveXamlFontSource(pinId, YYFontFamily.Serif.WindowsFamily());
                tileEntries.Add((pinId, disp, Font.Serif, pinId, fam, false));
            }
            else if (pinId.StartsWith("sys:", StringComparison.Ordinal))
            {
                string fam = FontShelfService.CleanRegistryFontFamilyName(pinId[4..]);
                if (string.IsNullOrEmpty(fam)) continue;
                tileEntries.Add((pinId, fam, Font.Serif, $"local:{fam}", $"{fam}, {YYFontFamily.Serif.WindowsFamily()}", false));
            }
            else if (_library.Fonts.Catalog.FirstOrDefault(c => c.Id == pinId) is { } pk)
            {
                string fallbackFam = pk.Kind == FontKind.Song ? YYFontFamily.Serif.WindowsFamily() : YYFontFamily.Sans.WindowsFamily();
                string fam = _library.Fonts.ResolveXamlFontSource(pk.Id, fallbackFam);
                tileEntries.Add((pinId, pk.Name, pk.Kind == FontKind.Song ? Font.Serif : Font.Sans, pk.Id, fam, false));
            }
        }

        int fontCols = 5;
        int fontTotal = tileEntries.Count + 1;
        int fontRows = (fontTotal + fontCols - 1) / fontCols;
        var fontGrid = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
        for (int i = 0; i < fontCols; i++) fontGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int r = 0; r < fontRows; r++) fontGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var resolvedFontPreset = PaperAppearance.Resolve(_library, rs).Preset;
        bool defaultFontSans = resolvedFontPreset is Paper.Plain or Paper.Dark;
        bool matchedTile = false;
        var downloadingTileLabels = new List<(string PackId, string BaseName, TextBlock Label)>();

        for (int i = 0; i < tileEntries.Count; i++)
        {
            var fc = tileEntries[i];
            bool on = string.IsNullOrEmpty(fc.Face)
                ? (string.IsNullOrEmpty(rs.FontFace) && (rs.Font == fc.Kind || (rs.Font is null && fc.Id == (defaultFontSans ? "system" : "serif"))))
                : (string.Equals(rs.FontFace, fc.Face, StringComparison.OrdinalIgnoreCase) ||
                   (fc.Face.StartsWith("local:", StringComparison.Ordinal) && string.Equals(rs.FontFace, fc.Face[6..], StringComparison.OrdinalIgnoreCase)));
            if (on) matchedTile = true;

            bool isDl = _library.Fonts.IsDownloading(fc.Id);
            bool needsDownload = _library.Fonts.Catalog.Any(c => c.Id == fc.Id) && !_library.Fonts.IsPackInstalled(fc.Id) && !isDl;
            var tile = new StackPanel { Spacing = 3, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            if (needsDownload && _library.Fonts.Catalog.FirstOrDefault(c => c.Id == fc.Id) is { } dlPack)
            {
                ulong bTotal = 0;
                foreach (var f in dlPack.Files) bTotal += f.Bytes;
                int mb = Math.Max(1, (int)Math.Round(bTotal / 1048576.0));
                tile.Children.Add(new TextBlock { Text = $"↓ {mb} MB", FontSize = 9.5, Foreground = ThemeHelper.Brush(p.T3), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, -2, 0, -2) });
            }
            tile.Children.Add(new TextBlock { Text = "永", FontFamily = new FontFamily(fc.Fam), FontSize = 20, Foreground = ThemeHelper.Brush(needsDownload ? p.T3 : p.T1), HorizontalAlignment = HorizontalAlignment.Center });
            var lblTb = new TextBlock
            {
                Text = isDl ? $"下载中 {(int)Math.Round(_library.Fonts.GetProgress(fc.Id) * 100)}%" : fc.BaseName,
                FontSize = 10.5,
                FontWeight = on ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                Foreground = ThemeHelper.Brush(on ? p.T1 : p.T2),
                HorizontalAlignment = HorizontalAlignment.Center,
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            tile.Children.Add(lblTb);
            if (_library.Fonts.Catalog.Any(c => c.Id == fc.Id))
                downloadingTileLabels.Add((fc.Id, fc.BaseName, lblTb));

            MenuFlyout? tileCtx = null;
            if (!fc.BuiltIn)
            {
                tileCtx = new MenuFlyout();
                var unpinMi = new MenuFlyoutItem { Text = L10n.T("从格子里拿掉") };
                string pinIdCapture = fc.Id;
                bool activeCapture = on;
                unpinMi.Click += (_, _) =>
                {
                    _library.Prefs.PinnedFontIds.Remove(pinIdCapture);
                    _library.SavePrefs();
                    if (activeCapture)
                        Update("字体", defaultFontSans ? "系统" : "宋体", s => s with { Font = null, FontFace = null });
                    RenderDynamicPage("reading");
                };
                tileCtx.Items.Add(unpinMi);
            }

            var fb = new Button
            {
                Height = 64,
                Padding = new Thickness(4, 0, 4, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                CornerRadius = new CornerRadius(10),
                Background = ThemeHelper.Brush(p.Card),
                BorderBrush = ThemeHelper.Brush(on ? tint.Accent : p.Line),
                BorderThickness = new Thickness(on ? 1.5 : 0.75),
                UseSystemFocusVisuals = false,
                ContextFlyout = tileCtx,
                Content = tile,
            };
            var fCapture = fc;
            fb.Click += async (_, _) =>
            {
                var catPack = _library.Fonts.Catalog.FirstOrDefault(c => c.Id == fCapture.Id);
                if (catPack is not null && !_library.Fonts.IsPackInstalled(catPack.Id))
                {
                    try { await _library.Fonts.DownloadPackAsync(catPack.Id); }
                    catch (Exception ex) { await ShowInfoDialogAsync("字体下载失败", ex.Message); return; }
                }
                if (_library.Fonts.Catalog.Any(c => c.Id == fCapture.Id) && !_library.Fonts.IsPackInstalled(fCapture.Id))
                    return;
                Update("字体", fCapture.BaseName, s => s with { Font = fCapture.Kind, FontFace = fCapture.Face });
                RenderDynamicPage("reading");
            };
            Grid.SetRow(fb, i / fontCols);
            Grid.SetColumn(fb, i % fontCols);
            fontGrid.Children.Add(fb);
        }

        _fontProgressUpdater = () =>
        {
            foreach (var (packId, baseName, lbl) in downloadingTileLabels)
            {
                lbl.Text = _library.Fonts.IsDownloading(packId)
                    ? $"下载中 {(int)Math.Round(_library.Fonts.GetProgress(packId) * 100)}%"
                    : baseName;
            }
        };

        // 最后一格：更多字体（字样册）
        bool extraFontOn = !matchedTile && !string.IsNullOrEmpty(rs.FontFace);
        var moreSp = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        moreSp.Children.Add(new Border
        {
            Width = 28,
            Height = 26,
            CornerRadius = new CornerRadius(6),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0.75),
            Child = new TextBlock { Text = "永", FontFamily = new FontFamily("SimSun"), FontSize = 13, Foreground = ThemeHelper.Brush(p.T2), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        });
        moreSp.Children.Add(new TextBlock { Text = "更多字体", FontSize = 10.5, FontWeight = extraFontOn ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal, Foreground = ThemeHelper.Brush(extraFontOn ? p.T1 : p.T2), HorizontalAlignment = HorizontalAlignment.Center });

        var moreFontBtn = new Button
        {
            Height = 64,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            CornerRadius = new CornerRadius(10),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(extraFontOn ? tint.Accent : p.Line),
            BorderThickness = new Thickness(extraFontOn ? 1.5 : 0.75),
            UseSystemFocusVisuals = false,
            Content = moreSp,
        };
        moreFontBtn.Click += async (_, _) => await ShowSettingsFontBookAsync(Update);
        Grid.SetRow(moreFontBtn, tileEntries.Count / fontCols);
        Grid.SetColumn(moreFontBtn, tileEntries.Count % fontCols);
        fontGrid.Children.Add(moreFontBtn);
        fontWrap.Children.Add(fontGrid);

        // 3. 字号与行距
        var fsPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var fsLabel = new TextBlock { Text = $"{(int)Math.Round(rs.FontSize)}", FontSize = 12.5, Width = 24, Foreground = ThemeHelper.Brush(p.T2), VerticalAlignment = VerticalAlignment.Center };
        var fsSlider = new Slider { Minimum = 12, Maximum = 34, StepFrequency = 1, Value = Math.Clamp(rs.FontSize, 12, 34), Width = 176 };
        AutomationProperties.SetName(fsSlider, L10n.T("字号"));
        fsSlider.ValueChanged += (_, e) =>
        {
            int v = (int)Math.Round(e.NewValue);
            if ((int)Math.Round(rs.FontSize) == v) return;
            fsLabel.Text = v.ToString();
            Update("字号", v.ToString(), s => s with { FontSize = v });
        };
        fsPanel.Children.Add(fsSlider);
        fsPanel.Children.Add(fsLabel);

        string curLh = rs.LineHeight is <= 1.7 ? "tight" : rs.LineHeight is >= 2.05 ? "loose" : "normal";
        var lhSeg = MakeSegments(
            [("tight", "紧"), ("normal", "适中"), ("loose", "松")],
            curLh,
            key =>
            {
                double? lh = key switch { "tight" => 1.6, "loose" => 2.2, _ => null };
                Update("行距", key switch { "tight" => "紧", "loose" => "松", _ => "适中" }, s => s with { LineHeight = lh });
                RenderDynamicPage("reading");
            },
            width: 164);

        DynamicPageHost.Children.Add(MakeCard(
            paperWrap,
            fontWrap,
            MakeRow("字号", null, fsPanel),
            MakeRow("行距", null, lhSeg)));

        // 4. 翻页、双栏、对齐、缩进、标点挤压（已移除旧版全局墨水屏与灰度残留行，1:1 MacSettings.swift:1152-1170）
        var modeSeg = MakeSegments(
            [(LayoutMode.Paginated, "左右翻页"), (LayoutMode.Scrolled, "上下滚动")],
            rs.Mode,
            m => { Update("翻页方式", m == LayoutMode.Scrolled ? "上下滚动" : "左右翻页", s => s with { Mode = m }); RenderDynamicPage("reading"); },
            width: 172);

        DynamicPageHost.Children.Add(MakeCard(
            MakeRow("翻页方式", null, modeSeg),
            MakeRow("宽窗口下双栏显示", "左右并排显示两页", MakeSwitch(_library.Prefs.TwoColumns, v =>
            {
                bool prev = !v;
                _library.Prefs.TwoColumns = v;
                _library.SavePrefs();
                App.CurrentApp.BroadcastReaderSettingsChanged();
                RecordPrefChange(
                    nameof(DevicePrefs.TwoColumns),
                    "双栏显示",
                    v ? "开" : "关",
                    prev ? "开" : "关",
                    prev,
                    v,
                    undo: () => { _library.Prefs.TwoColumns = prev; _library.SavePrefs(); App.CurrentApp.BroadcastReaderSettingsChanged(); },
                    redo: () => { _library.Prefs.TwoColumns = v; _library.SavePrefs(); App.CurrentApp.BroadcastReaderSettingsChanged(); });
            })),
            MakeRow("两端对齐", null, MakeSwitch(rs.Justify ?? true, v => Update("两端对齐", v ? "开" : "关", s => s with { Justify = v }))),
            MakeRow("首行缩进两字", null, MakeSwitch(rs.Indent ?? true, v => Update("首行缩进", v ? "开" : "关", s => s with { Indent = v }))),
            MakeRow("中文标点挤压", "收紧连续标点的留白", MakeSwitch(rs.Compress, v => Update("标点挤压", v ? "开" : "关", s => s with { Compress = v })))));

        // 5. 沉浸式阅读
        DynamicPageHost.Children.Add(MakeHeading("沉浸式阅读"));
        DynamicPageHost.Children.Add(MakeCard(
            MakeRow("进入与退出", "全屏隐藏界面，专注书页正文", new TextBlock { Text = "F11 或 Esc", FontSize = 12, Foreground = ThemeHelper.Brush(p.T2) }),
            MakeRow("打开书籍时自动进入", null, MakeSwitch(_library.Prefs.ImmersiveAutoEnter, v =>
            {
                bool prev = !v;
                _library.Prefs.ImmersiveAutoEnter = v;
                _library.SavePrefs();
                RecordPrefChange(
                    nameof(DevicePrefs.ImmersiveAutoEnter),
                    "自动沉浸阅读",
                    v ? "开" : "关",
                    prev ? "开" : "关",
                    prev,
                    v,
                    undo: () => { _library.Prefs.ImmersiveAutoEnter = prev; _library.SavePrefs(); },
                    redo: () => { _library.Prefs.ImmersiveAutoEnter = v; _library.SavePrefs(); });
            })),
            MakeRow("底部保留进度细线", null, MakeSwitch(_library.Prefs.ImmersiveKeepLine, v =>
            {
                bool prev = !v;
                _library.Prefs.ImmersiveKeepLine = v;
                _library.SavePrefs();
                RecordPrefChange(
                    nameof(DevicePrefs.ImmersiveKeepLine),
                    "沉浸进度细线",
                    v ? "开" : "关",
                    prev ? "开" : "关",
                    prev,
                    v,
                    undo: () => { _library.Prefs.ImmersiveKeepLine = prev; _library.SavePrefs(); },
                    redo: () => { _library.Prefs.ImmersiveKeepLine = v; _library.SavePrefs(); });
            })),
            MakeRow("隐藏划线与想法", "退出沉浸后恢复显示", MakeSwitch(_library.Prefs.ImmersiveHideMarks, v =>
            {
                bool prev = !v;
                _library.Prefs.ImmersiveHideMarks = v;
                _library.SavePrefs();
                RecordPrefChange(
                    nameof(DevicePrefs.ImmersiveHideMarks),
                    "隐藏划线与想法",
                    v ? "开" : "关",
                    prev ? "开" : "关",
                    prev,
                    v,
                    undo: () => { _library.Prefs.ImmersiveHideMarks = prev; _library.SavePrefs(); },
                    redo: () => { _library.Prefs.ImmersiveHideMarks = v; _library.SavePrefs(); });
            })),
            MakeRow("隐藏书伴提示与难词注释", null, MakeSwitch(_library.Prefs.ImmersiveHideHints, v =>
            {
                bool prev = !v;
                _library.Prefs.ImmersiveHideHints = v;
                _library.SavePrefs();
                RecordPrefChange(
                    nameof(DevicePrefs.ImmersiveHideHints),
                    "隐藏提示与注释",
                    v ? "开" : "关",
                    prev ? "开" : "关",
                    prev,
                    v,
                    undo: () => { _library.Prefs.ImmersiveHideHints = prev; _library.SavePrefs(); },
                    redo: () => { _library.Prefs.ImmersiveHideHints = v; _library.SavePrefs(); });
            })),
            MakeRow("关闭翻页动画", "直接切换整页，无滑动过渡", MakeSwitch(_library.Prefs.ImmersiveNoAnimation, v =>
            {
                bool prev = !v;
                _library.Prefs.ImmersiveNoAnimation = v;
                _library.SavePrefs();
                RecordPrefChange(
                    nameof(DevicePrefs.ImmersiveNoAnimation),
                    "关闭翻页动画",
                    v ? "开" : "关",
                    prev ? "开" : "关",
                    prev,
                    v,
                    undo: () => { _library.Prefs.ImmersiveNoAnimation = prev; _library.SavePrefs(); },
                    redo: () => { _library.Prefs.ImmersiveNoAnimation = v; _library.SavePrefs(); });
            }))));
    }

    private sealed record FontBookItem(
        string Id,
        string Name,
        string GroupKey, // "catalog" | "custom" | "pc" | "builtin"
        FontKind Kind,
        string XamlFamily,
        string Subtitle,
        string DetailNote,
        ulong SizeBytes,
        bool NeedsDownload,
        bool CanDelete);

    private async Task ShowSettingsFontBookAsync(Action<string, string, Func<ReaderSettings, ReaderSettings>> updateFn)
    {
        var p = CurrentPalette;
        var tint = CurrentTint;
        ContentDialog? dlgRef = null;

        var rs = _library.GetReaderSettings();
        var resolvedPreset = PaperAppearance.Resolve(_library, rs).Preset;
        bool defaultSans = resolvedPreset is Paper.Plain or Paper.Dark;
        string currentActiveId = !string.IsNullOrEmpty(rs.FontFace)
            ? (rs.FontFace.StartsWith("local:", StringComparison.OrdinalIgnoreCase) ? $"sys:{rs.FontFace[6..]}" : rs.FontFace)
            : rs.Font switch
            {
                Font.Sans => "system",
                Font.Serif => "serif",
                Font.Kai => "kai",
                Font.WenKai => "wenkai",
                _ => defaultSans ? "system" : "serif",
            };

        string selectedCategory = "all"; // all | song | kai | hei | fang | hand | pc
        string selectedFontId = currentActiveId;
        string searchQuery = "";
        string defaultSpecimen = "我冒了严寒，回到相隔二千余里，别了二十余年的故乡去。时候既然是深冬；渐近故乡时，天气又阴晦了，冷风吹进船舱中，呜呜的响。";

        IReadOnlyList<SystemFontEntry> sysFonts = _library.Fonts.CachedSystemCjkFonts ?? [];
        bool loadingSysFonts = _library.Fonts.CachedSystemCjkFonts is null;

        static string KindLabel(FontKind k) => k switch
        {
            FontKind.Song => "宋",
            FontKind.Kai => "楷",
            FontKind.Hei => "黑",
            FontKind.Fang => "仿宋",
            FontKind.Hand => "手写",
            _ => "黑",
        };

        List<FontBookItem> BuildAllItems()
        {
            var items = new List<FontBookItem>();

            // 1. 可以下载 / 开源字包（Catalog）
            foreach (var pk in _library.Fonts.Catalog)
            {
                bool inst = _library.Fonts.IsPackInstalled(pk.Id);
                ulong bytes = 0;
                foreach (var f in pk.Files) bytes += f.Bytes;
                int mb = Math.Max(1, (int)Math.Round(bytes / 1048576.0));
                string fallbackFam = pk.Id switch
                {
                    "wenkai" => YYFontFamily.Wenkai.WindowsFamily(),
                    "notosans" => YYFontFamily.Sans.WindowsFamily(),
                    _ => YYFontFamily.Serif.WindowsFamily(),
                };
                string fam = _library.Fonts.ResolveXamlFontSource(pk.Id, fallbackFam);
                string kl = KindLabel(pk.Kind);
                items.Add(new FontBookItem(
                    pk.Id,
                    pk.Name,
                    "catalog",
                    pk.Kind,
                    fam,
                    $"开源 · {kl}",
                    $"{kl} · 开源（{pk.License}） · {mb} MB",
                    bytes,
                    NeedsDownload: !inst,
                    CanDelete: inst));
            }

            // 2. 你添加的（Custom files）
            foreach (string fn in _library.Fonts.GetCustomFontFiles())
            {
                string id = $"file:{fn}";
                string disp = _library.Fonts.GetFontFileDisplayName(fn);
                var kind = _library.Fonts.GetFontKind(id);
                string kl = KindLabel(kind);
                string fam = _library.Fonts.ResolveXamlFontSource(id, YYFontFamily.Serif.WindowsFamily());
                items.Add(new FontBookItem(
                    id,
                    disp,
                    "custom",
                    kind,
                    fam,
                    $"你添加的 · {kl}",
                    $"{kl} · 你添加的（{fn}）",
                    0,
                    NeedsDownload: false,
                    CanDelete: true));
            }

            // 3. 这台电脑（System CJK fonts）
            foreach (var sf in sysFonts)
            {
                string id = $"sys:{sf.FamilyName}";
                var kind = FontShelfService.ClassifyFontKind($"{sf.DisplayName} {sf.FamilyName}");
                string kl = KindLabel(kind);
                items.Add(new FontBookItem(
                    id,
                    sf.DisplayName,
                    "pc",
                    kind,
                    $"{sf.FamilyName}, {YYFontFamily.Serif.WindowsFamily()}",
                    $"这台电脑 · {kl}",
                    $"{kl} · 这台电脑上的",
                    0,
                    NeedsDownload: false,
                    CanDelete: false));
            }

            // 4. 自带（Built-in）
            items.Add(new FontBookItem("serif", "宋体", "builtin", FontKind.Song, YYFontFamily.Serif.WindowsFamily(), "自带 · 宋", "宋 · 语阅自带", 0, false, false));
            items.Add(new FontBookItem("kai", "楷体", "builtin", FontKind.Kai, YYFontFamily.Kaiti.WindowsFamily(), "自带 · 楷", "楷 · 语阅自带", 0, false, false));
            items.Add(new FontBookItem("system", "系统默认", "builtin", FontKind.Hei, YYFontFamily.Sans.WindowsFamily(), "自带 · 黑", "黑 · 语阅自带", 0, false, false));

            return items;
        }

        // Root 800x520 frameless 3-column sheet + bottom footer
        var rootGrid = new Grid { Width = 800, Height = 520, Background = ThemeHelper.Brush(p.Card) };
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0.5) });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });

        var colsGrid = new Grid();
        colsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        colsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.5) });
        colsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(262) });
        colsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.5) });
        colsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Column 1 (150px): Categories
        var col1Stack = new StackPanel { Spacing = 2, Padding = new Thickness(10, 16, 10, 12) };
        var col1Host = new Border
        {
            Background = ThemeHelper.Brush(IsDark ? Color.FromArgb(255, 28, 29, 33) : Color.FromArgb(255, 246, 244, 238)),
            Child = col1Stack,
        };
        colsGrid.Children.Add(col1Host);
        var sep1 = new Border { Background = ThemeHelper.Brush(p.Line) };
        Grid.SetColumn(sep1, 1);
        colsGrid.Children.Add(sep1);

        // Column 2 (262px): Search + Grouped Font Rows
        var col2Grid = new Grid { Background = ThemeHelper.Brush(p.Card) };
        col2Grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        col2Grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0.5) });
        col2Grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var searchWrap = new Grid
        {
            Margin = new Thickness(10, 10, 10, 10),
            Height = 30,
            Padding = new Thickness(9, 0, 8, 0),
            CornerRadius = new CornerRadius(8),
            Background = ThemeHelper.Brush(p.Inset),
            ColumnSpacing = 6,
        };
        searchWrap.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        searchWrap.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        searchWrap.Children.Add(YYIcons.Make("search", 12, ThemeHelper.Brush(p.T3)));

        var searchBox = new TextBox
        {
            PlaceholderText = "搜字体名称…",
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(0, 4, 0, 4),
        };
        YYChrome.StripTextBoxChrome(searchBox);
        AutomationProperties.SetName(searchBox, L10n.T("搜字体名称"));
        Grid.SetColumn(searchBox, 1);
        searchWrap.Children.Add(searchBox);
        col2Grid.Children.Add(searchWrap);

        var searchSep = new Border { Background = ThemeHelper.Brush(p.Line) };
        Grid.SetRow(searchSep, 1);
        col2Grid.Children.Add(searchSep);

        var fontListStack = new StackPanel { Spacing = 2, Padding = new Thickness(8, 6, 8, 10) };
        var fontListScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = fontListStack };
        Grid.SetRow(fontListScroll, 2);
        col2Grid.Children.Add(fontListScroll);
        Grid.SetColumn(col2Grid, 2);
        colsGrid.Children.Add(col2Grid);

        var sep2 = new Border { Background = ThemeHelper.Brush(p.Line) };
        Grid.SetColumn(sep2, 3);
        colsGrid.Children.Add(sep2);

        // Column 3 (*): Warm paper #F5F4ED / dark card specimen with 104x104 red dashed 米字格
        Color specimenPaperBg = IsDark ? Color.FromArgb(255, 29, 30, 34) : Color.FromArgb(255, 245, 244, 237);
        var col3Grid = new Grid { Background = ThemeHelper.Brush(specimenPaperBg), Padding = new Thickness(22, 20, 22, 14) };
        col3Grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        col3Grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        col3Grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Top: 104x104 米字格 + 22pt Font Name & License/Size
        var topSpecimenHead = new Grid { ColumnSpacing = 18, Margin = new Thickness(0, 0, 0, 14) };
        topSpecimenHead.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
        topSpecimenHead.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        Color redOuter = IsDark ? Color.FromArgb(115, 214, 92, 82) : Color.FromArgb(88, 194, 69, 58);
        Color redCross = IsDark ? Color.FromArgb(72, 214, 92, 82) : Color.FromArgb(50, 194, 69, 58);
        Color redDiag = IsDark ? Color.FromArgb(48, 214, 92, 82) : Color.FromArgb(34, 194, 69, 58);

        var miziGrid = new Grid { Width = 104, Height = 104 };
        miziGrid.Children.Add(new Microsoft.UI.Xaml.Shapes.Line { X1 = 0, Y1 = 52, X2 = 104, Y2 = 52, Stroke = ThemeHelper.Brush(redCross), StrokeThickness = 0.85, StrokeDashArray = [3, 2] });
        miziGrid.Children.Add(new Microsoft.UI.Xaml.Shapes.Line { X1 = 52, Y1 = 0, X2 = 52, Y2 = 104, Stroke = ThemeHelper.Brush(redCross), StrokeThickness = 0.85, StrokeDashArray = [3, 2] });
        miziGrid.Children.Add(new Microsoft.UI.Xaml.Shapes.Line { X1 = 0, Y1 = 0, X2 = 104, Y2 = 104, Stroke = ThemeHelper.Brush(redDiag), StrokeThickness = 0.75, StrokeDashArray = [3, 3] });
        miziGrid.Children.Add(new Microsoft.UI.Xaml.Shapes.Line { X1 = 104, Y1 = 0, X2 = 0, Y2 = 104, Stroke = ThemeHelper.Brush(redDiag), StrokeThickness = 0.75, StrokeDashArray = [3, 3] });
        var yongGlyph = new TextBlock
        {
            Text = "永",
            FontSize = 76,
            Foreground = ThemeHelper.Brush(p.T1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, -4, 0, 0),
        };
        miziGrid.Children.Add(yongGlyph);
        miziGrid.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(4),
            BorderBrush = ThemeHelper.Brush(redOuter),
            BorderThickness = new Thickness(1),
            IsHitTestVisible = false,
        });
        topSpecimenHead.Children.Add(miziGrid);

        var specimenMetaStack = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        var specimenTitleTb = new TextBlock
        {
            FontSize = 22,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = ThemeHelper.Brush(p.T1),
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.Wrap,
        };
        var specimenSubTb = new TextBlock
        {
            FontSize = 11.5,
            Foreground = ThemeHelper.Brush(p.T3),
            TextWrapping = TextWrapping.Wrap,
        };
        var dlProgressPanel = new StackPanel { Spacing = 4, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 2, 0, 0) };
        var dlProgressTb = new TextBlock { FontSize = 11, Foreground = ThemeHelper.Brush(tint.Accent) };
        var dlProgressBar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 4 };
        dlProgressPanel.Children.Add(dlProgressTb);
        dlProgressPanel.Children.Add(dlProgressBar);
        var dlErrorTb = new TextBlock
        {
            FontSize = 11.5,
            Foreground = ThemeHelper.Brush(p.Danger),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 2, 0, 0),
        };

        specimenMetaStack.Children.Add(specimenTitleTb);
        specimenMetaStack.Children.Add(specimenSubTb);
        specimenMetaStack.Children.Add(dlProgressPanel);
        specimenMetaStack.Children.Add(dlErrorTb);
        Grid.SetColumn(specimenMetaStack, 1);
        topSpecimenHead.Children.Add(specimenMetaStack);

        var topHeadWrap = new StackPanel { Spacing = 0 };
        topHeadWrap.Children.Add(topSpecimenHead);
        topHeadWrap.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(0, 0, 0, 12) });
        col3Grid.Children.Add(topHeadWrap);

        // Middle: Editable《故乡》specimen TextBox
        var specimenEditor = new TextBox
        {
            Text = defaultSpecimen,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 16.5,
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            Foreground = ThemeHelper.Brush(p.T1),
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        YYChrome.StripTextBoxChrome(specimenEditor);
        specimenEditor.Foreground = ThemeHelper.Brush(p.T1);
        AutomationProperties.SetName(specimenEditor, L10n.T("字体预览样张"));
        Grid.SetRow(specimenEditor, 1);
        col3Grid.Children.Add(specimenEditor);

        // Bottom specimen bar: 春眠不觉晓 · 天地玄黄 · 0123456789 · Aa Bb
        var specimenFootStack = new StackPanel { Spacing = 6, Margin = new Thickness(0, 10, 0, 0) };
        specimenFootStack.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line) });
        var alphabetBarTb = new TextBlock
        {
            Text = "春眠不觉晓 · 天地玄黄 · 0123456789 · Aa Bb",
            FontSize = 13,
            Foreground = ThemeHelper.Brush(p.T2),
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var specimenHintTb = new TextBlock
        {
            Text = "点上面这段字可以改成你想看的；换字体时它跟着走",
            FontSize = 10.5,
            Foreground = ThemeHelper.Brush(p.T3),
        };
        specimenFootStack.Children.Add(alphabetBarTb);
        specimenFootStack.Children.Add(specimenHintTb);
        Grid.SetRow(specimenFootStack, 2);
        col3Grid.Children.Add(specimenFootStack);

        Grid.SetColumn(col3Grid, 4);
        colsGrid.Children.Add(col3Grid);
        rootGrid.Children.Add(colsGrid);

        // Footer separator + Footer bar
        var footSep = new Border { Background = ThemeHelper.Brush(p.Line) };
        Grid.SetRow(footSep, 1);
        rootGrid.Children.Add(footSep);

        var footerGrid = new Grid { Padding = new Thickness(16, 0, 16, 0), Background = ThemeHelper.Brush(p.Card) };
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var importFileBtn = YYChrome.MakeSheetPillButton("从文件添加…", () => { }, IsDark, _library.Prefs.Tint, kind: "secondary", height: 34, horizontalPadding: 14);
        importFileBtn.VerticalAlignment = VerticalAlignment.Center;
        footerGrid.Children.Add(importFileBtn);

        bool fontBookClosed = false;
        var sessionDownloads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fontDownloadErrors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void CancelInFlightFontBookDownloads()
        {
            foreach (string pid in sessionDownloads.ToArray())
            {
                sessionDownloads.Remove(pid);
                if (_library.Fonts.IsDownloading(pid))
                    _library.Fonts.CancelDownload(pid);
            }
        }

        var rightFootBtns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        var cancelSheetBtn = YYChrome.MakeSheetPillButton("取消", () =>
        {
            fontBookClosed = true;
            CancelInFlightFontBookDownloads();
            dlgRef?.Hide();
        }, IsDark, _library.Prefs.Tint, kind: "secondary", height: 34, horizontalPadding: 16);
        var applyFontBtn = YYChrome.MakeSheetPillButton("选用", () => { }, IsDark, _library.Prefs.Tint, kind: "dark", height: 34, horizontalPadding: 18);
        rightFootBtns.Children.Add(cancelSheetBtn);
        rightFootBtns.Children.Add(applyFontBtn);
        Grid.SetColumn(rightFootBtns, 2);
        footerGrid.Children.Add(rightFootBtns);
        Grid.SetRow(footerGrid, 2);
        rootGrid.Children.Add(footerGrid);

        void SetApplyFontButtonLabel(string label)
        {
            if (applyFontBtn.Content is TextBlock tb)
                tb.Text = L10n.T(label);
            else
                applyFontBtn.Content = L10n.T(label);
        }

        void PinIfExternal(string id)
        {
            if (id is "system" or "serif" or "kai" or "wenkai") return;
            if (!_library.Prefs.PinnedFontIds.Contains(id))
            {
                if (_library.Prefs.PinnedFontIds.Count >= 4)
                    _library.Prefs.PinnedFontIds.RemoveAt(0);
                _library.Prefs.PinnedFontIds.Add(id);
                _library.SavePrefs();
            }
        }

        void RebuildFontBookAndTree()
        {
            if (fontBookClosed || _closed) return;
            RebuildFontBook();
            L10n.ApplyTree(col1Stack);
            L10n.ApplyTree(fontListStack);
        }

        async Task ApplySelectedFontAsync(FontBookItem item)
        {
            if (item.GroupKey == "catalog" && !_library.Fonts.IsPackInstalled(item.Id))
            {
                if (_library.Fonts.IsDownloading(item.Id))
                {
                    sessionDownloads.Remove(item.Id);
                    _library.Fonts.CancelDownload(item.Id);
                    RebuildFontBookAndTree();
                    return;
                }

                fontDownloadErrors.Remove(item.Id);
                sessionDownloads.Add(item.Id);
                RebuildFontBookAndTree();
                try
                {
                    await _library.Fonts.DownloadPackAsync(item.Id);
                }
                catch (Exception ex)
                {
                    sessionDownloads.Remove(item.Id);
                    if (!fontBookClosed && !_closed)
                    {
                        fontDownloadErrors[item.Id] = ex.Message;
                        RebuildFontBookAndTree();
                    }
                    return;
                }

                bool wasActiveSession = sessionDownloads.Remove(item.Id);
                if (fontBookClosed || _closed || !wasActiveSession || !_library.Fonts.IsPackInstalled(item.Id))
                {
                    if (!fontBookClosed && !_closed)
                        RebuildFontBookAndTree();
                    return;
                }
            }

            if (fontBookClosed || _closed) return;
            PinIfExternal(item.Id);
            updateFn("字体", item.Name, s => item.Id switch
            {
                "system" => s with { Font = Font.Sans, FontFace = null },
                "serif" => s with { Font = Font.Serif, FontFace = null },
                "kai" => s with { Font = Font.Kai, FontFace = null },
                "wenkai" => s with { Font = Font.WenKai, FontFace = null },
                _ when item.Id.StartsWith("sys:", StringComparison.OrdinalIgnoreCase) =>
                    s with { Font = item.Kind == FontKind.Hei ? Font.Sans : Font.Serif, FontFace = $"local:{item.Id[4..]}" },
                _ when item.Id.StartsWith("file:", StringComparison.OrdinalIgnoreCase) =>
                    s with { Font = item.Kind == FontKind.Hei ? Font.Sans : Font.Serif, FontFace = item.Id },
                _ => s with { Font = item.Kind == FontKind.Hei ? Font.Sans : Font.Serif, FontFace = item.Id },
            });
            fontBookClosed = true;
            dlgRef?.Hide();
            RenderDynamicPage("reading");
        }

        void UpdateSpecimenPane(FontBookItem? item)
        {
            if (item is null) return;
            var ff = new FontFamily(item.XamlFamily);
            yongGlyph.FontFamily = ff;
            specimenTitleTb.Text = item.Name;
            specimenTitleTb.FontFamily = ff;
            specimenSubTb.Text = L10n.T(item.DetailNote);
            specimenEditor.FontFamily = ff;
            alphabetBarTb.FontFamily = ff;

            bool isDl = item.GroupKey == "catalog" && _library.Fonts.IsDownloading(item.Id);
            bool isInstalled = item.GroupKey != "catalog" || _library.Fonts.IsPackInstalled(item.Id);
            bool isCurrent = string.Equals(item.Id, currentActiveId, StringComparison.OrdinalIgnoreCase) && isInstalled;

            if (isDl)
            {
                int pct = (int)Math.Round(_library.Fonts.GetProgress(item.Id) * 100);
                dlProgressPanel.Visibility = Visibility.Visible;
                dlProgressTb.Text = L10n.T($"正在下载字体包 {pct}%…");
                dlProgressBar.Value = pct;
                dlErrorTb.Visibility = Visibility.Collapsed;
                SetApplyFontButtonLabel($"取消下载 ({pct}%)");
                applyFontBtn.IsEnabled = true;
            }
            else
            {
                dlProgressPanel.Visibility = Visibility.Collapsed;
                if (!isInstalled && fontDownloadErrors.TryGetValue(item.Id, out var errMsg) && !string.IsNullOrWhiteSpace(errMsg))
                {
                    dlErrorTb.Text = errMsg;
                    dlErrorTb.Visibility = Visibility.Visible;
                    SetApplyFontButtonLabel("重试下载");
                    applyFontBtn.IsEnabled = true;
                }
                else
                {
                    dlErrorTb.Visibility = Visibility.Collapsed;
                    if (isCurrent)
                    {
                        SetApplyFontButtonLabel("正在用");
                        applyFontBtn.IsEnabled = false;
                    }
                    else if (!isInstalled)
                    {
                        int mb = Math.Max(1, (int)Math.Round(item.SizeBytes / 1048576.0));
                        SetApplyFontButtonLabel($"下载并选用 · {mb} MB");
                        applyFontBtn.IsEnabled = true;
                    }
                    else
                    {
                        SetApplyFontButtonLabel("选用");
                        applyFontBtn.IsEnabled = true;
                    }
                }
            }
        }

        void RebuildFontBook()
        {
            var allItems = BuildAllItems();
            if (allItems.All(x => !string.Equals(x.Id, selectedFontId, StringComparison.OrdinalIgnoreCase)) && allItems.Count > 0)
                selectedFontId = allItems[0].Id;

            // Column 1: 7 categories with character/laptop badges and counts
            col1Stack.Children.Clear();
            col1Stack.Children.Add(new TextBlock
            {
                Text = L10n.T("字样册"),
                FontSize = 15,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = ThemeHelper.Brush(p.T1),
                Margin = new Thickness(8, 0, 0, 10),
            });

            (string Key, string Label, string BadgeChar, int Count)[] cats =
            [
                ("all", "全部", "字", allItems.Count),
                ("song", "宋", "宋", allItems.Count(x => x.Kind == FontKind.Song)),
                ("kai", "楷", "楷", allItems.Count(x => x.Kind == FontKind.Kai)),
                ("hei", "黑", "黑", allItems.Count(x => x.Kind == FontKind.Hei)),
                ("fang", "仿宋", "仿", allItems.Count(x => x.Kind == FontKind.Fang)),
                ("hand", "手写", "手", allItems.Count(x => x.Kind == FontKind.Hand)),
                ("---", "", "", 0),
                ("pc", "这台电脑", "pc", allItems.Count(x => x.GroupKey == "pc")),
            ];

            foreach (var (cKey, cLbl, cBadge, cCnt) in cats)
            {
                if (cKey == "---")
                {
                    col1Stack.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(8, 6, 8, 6) });
                    continue;
                }
                bool activeCat = selectedCategory == cKey;
                var row = new Grid { ColumnSpacing = 8, Padding = new Thickness(6, 5, 8, 5) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                UIElement badgeChild = cBadge == "pc"
                    ? YYIcons.Make("laptop", 11, ThemeHelper.Brush(activeCat ? tint.Accent : p.T2))
                    : L10n.Skip(new TextBlock
                    {
                        Text = cBadge,
                        FontFamily = new FontFamily(cKey == "hei" ? YYFontFamily.Sans.WindowsFamily() : (cKey == "kai" ? YYFontFamily.Kaiti.WindowsFamily() : YYFontFamily.Serif.WindowsFamily())),
                        FontSize = 10.5,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        Foreground = ThemeHelper.Brush(activeCat ? tint.Accent : p.T2),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    });

                row.Children.Add(new Border
                {
                    Width = 20,
                    Height = 20,
                    CornerRadius = new CornerRadius(5),
                    Background = ThemeHelper.Brush(activeCat ? tint.Soft : p.Card),
                    BorderBrush = ThemeHelper.Brush(activeCat ? tint.Edge : p.Line),
                    BorderThickness = new Thickness(0.5),
                    Child = badgeChild,
                });

                var nameTb = new TextBlock
                {
                    Text = L10n.T(cLbl),
                    FontSize = 12,
                    FontWeight = activeCat ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                    Foreground = ThemeHelper.Brush(activeCat ? p.T1 : p.T2),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(nameTb, 1);
                row.Children.Add(nameTb);

                var cntTb = new TextBlock
                {
                    Text = (cKey == "pc" && loadingSysFonts) ? "…" : cCnt.ToString(),
                    FontSize = 10.5,
                    Foreground = ThemeHelper.Brush(p.T3),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(cntTb, 2);
                row.Children.Add(cntTb);

                var catBtn = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Padding = new Thickness(0),
                    CornerRadius = new CornerRadius(7),
                    BorderThickness = new Thickness(0),
                    Background = activeCat ? ThemeHelper.Brush(Color.FromArgb(18, p.T1.R, p.T1.G, p.T1.B)) : new SolidColorBrush(Colors.Transparent),
                    UseSystemFocusVisuals = false,
                    Content = row,
                };
                string capKey = cKey;
                catBtn.Click += (_, _) =>
                {
                    selectedCategory = capKey;
                    RebuildFontBook();
                };
                col1Stack.Children.Add(catBtn);
            }

            // Column 2: Filtered + Grouped Font Rows
            fontListStack.Children.Clear();
            string q = searchQuery.Trim();
            var filtered = allItems.Where(item =>
            {
                bool catMatch = selectedCategory switch
                {
                    "song" => item.Kind == FontKind.Song,
                    "kai" => item.Kind == FontKind.Kai,
                    "hei" => item.Kind == FontKind.Hei,
                    "fang" => item.Kind == FontKind.Fang,
                    "hand" => item.Kind == FontKind.Hand,
                    "pc" => item.GroupKey == "pc",
                    _ => true,
                };
                if (!catMatch) return false;
                if (string.IsNullOrEmpty(q)) return true;
                return item.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || item.Id.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || item.Subtitle.Contains(q, StringComparison.OrdinalIgnoreCase);
            }).ToArray();

            if (filtered.Length > 0 && filtered.All(x => !string.Equals(x.Id, selectedFontId, StringComparison.OrdinalIgnoreCase)))
                selectedFontId = filtered[0].Id;

            void AddFontRow(FontBookItem item)
            {
                bool sel = string.Equals(item.Id, selectedFontId, StringComparison.OrdinalIgnoreCase);
                bool activeInReader = string.Equals(item.Id, currentActiveId, StringComparison.OrdinalIgnoreCase) && !item.NeedsDownload;
                bool dl = item.GroupKey == "catalog" && _library.Fonts.IsDownloading(item.Id);

                var rGrid = new Grid { ColumnSpacing = 8, Padding = new Thickness(10, 7, 10, 7) };
                rGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                rGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var left = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
                left.Children.Add(new TextBlock
                {
                    Text = item.Name,
                    FontFamily = new FontFamily(item.XamlFamily),
                    FontSize = 16.5,
                    Foreground = ThemeHelper.Brush(p.T1),
                    MaxLines = 1,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
                left.Children.Add(new TextBlock
                {
                    Text = L10n.T(item.Subtitle),
                    FontSize = 10.5,
                    Foreground = ThemeHelper.Brush(p.T3),
                });
                rGrid.Children.Add(left);

                FrameworkElement? rightBadge = null;
                if (dl)
                {
                    rightBadge = new ProgressRing { Width = 14, Height = 14, IsActive = true, Foreground = ThemeHelper.Brush(tint.Accent), VerticalAlignment = VerticalAlignment.Center };
                }
                else if (activeInReader)
                {
                    var chk = YYIcons.Make("checkmark", 12, ThemeHelper.Brush(tint.Accent));
                    chk.VerticalAlignment = VerticalAlignment.Center;
                    rightBadge = chk;
                }
                else if (item.NeedsDownload)
                {
                    int mb = Math.Max(1, (int)Math.Round(item.SizeBytes / 1048576.0));
                    rightBadge = new TextBlock
                    {
                        Text = $"↓ {mb}M",
                        FontSize = 10.5,
                        Foreground = ThemeHelper.Brush(p.T3),
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                }
                if (rightBadge is not null)
                {
                    Grid.SetColumn(rightBadge, 1);
                    rGrid.Children.Add(rightBadge);
                }

                MenuFlyout? rowCtx = null;
                if (item.CanDelete)
                {
                    rowCtx = new MenuFlyout();
                    var delMi = new MenuFlyoutItem { Text = L10n.T("删除字体文件…"), Foreground = ThemeHelper.Brush(p.Danger) };
                    var capItem = item;
                    delMi.Click += async (_, _) =>
                    {
                        bool ok = await YYChrome.ShowConfirmAsync(
                            RootGrid.XamlRoot,
                            IsDark,
                            L10n.T($"删除字体「{capItem.Name}」？"),
                            L10n.T("字体文件会从语阅目录移除，需要时可重新下载或导入。"),
                            confirmText: L10n.T("删除"),
                            cancelText: L10n.T("取消"),
                            destructive: true,
                            tint: _library.Prefs.Tint);
                        if (!ok || _closed) return;

                        string? deletedFam = capItem.GroupKey == "custom" && capItem.Id.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                            ? _library.Fonts.GetFontFileFamily(capItem.Id[5..])
                            : _library.Fonts.Catalog.FirstOrDefault(c => c.Id == capItem.Id)?.PreviewFamily;

                        if (capItem.GroupKey == "custom" && capItem.Id.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                        {
                            string fn = capItem.Id[5..];
                            await Task.Run(() => _library.Fonts.DeleteCustomFont(fn));
                        }
                        else if (capItem.GroupKey == "catalog")
                        {
                            string packId = capItem.Id;
                            await Task.Run(() => _library.Fonts.DeletePack(packId));
                        }
                        if (_closed) return;

                        if (_library.Prefs.PinnedFontIds.Remove(capItem.Id))
                            _library.SavePrefs();

                        var curRs = _library.GetReaderSettings();
                        bool wasActive =
                            (!string.IsNullOrEmpty(curRs.FontFace) && (
                                string.Equals(curRs.FontFace, capItem.Id, StringComparison.OrdinalIgnoreCase) ||
                                (!string.IsNullOrEmpty(deletedFam) && string.Equals(curRs.FontFace, deletedFam, StringComparison.OrdinalIgnoreCase)) ||
                                (!string.IsNullOrEmpty(deletedFam) && string.Equals(curRs.FontFace, $"local:{deletedFam}", StringComparison.OrdinalIgnoreCase)))) ||
                            (string.Equals(capItem.Id, "wenkai", StringComparison.OrdinalIgnoreCase) && curRs.Font == Font.WenKai);

                        if (wasActive)
                        {
                            currentActiveId = "serif";
                            updateFn("字体", "宋体", s => s with { Font = Font.Serif, FontFace = null });
                        }
                        fontDownloadErrors.Remove(capItem.Id);
                        RenderDynamicPage("reading");
                        if (!fontBookClosed)
                            RebuildFontBookAndTree();
                    };
                    rowCtx.Items.Add(delMi);
                }

                var rowBtn = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Padding = new Thickness(0),
                    CornerRadius = new CornerRadius(8),
                    BorderThickness = new Thickness(sel ? 1 : 0),
                    BorderBrush = sel ? ThemeHelper.Brush(tint.Edge) : new SolidColorBrush(Colors.Transparent),
                    Background = sel ? ThemeHelper.Brush(tint.Soft) : new SolidColorBrush(Colors.Transparent),
                    UseSystemFocusVisuals = false,
                    ContextFlyout = rowCtx,
                    Content = rGrid,
                };
                var captured = item;
                rowBtn.Click += (_, _) =>
                {
                    selectedFontId = captured.Id;
                    RebuildFontBook();
                };
                rowBtn.DoubleTapped += async (_, _) => await ApplySelectedFontAsync(captured);
                fontListStack.Children.Add(rowBtn);
            }

            if (filtered.Length == 0)
            {
                fontListStack.Children.Add(new TextBlock
                {
                    Text = selectedCategory == "pc" && loadingSysFonts ? L10n.T("正在扫描本机中文字体…") : L10n.T("没有匹配的字体"),
                    FontSize = 12,
                    Foreground = ThemeHelper.Brush(p.T3),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 32, 0, 0),
                });
            }
            else if (selectedCategory == "all" && string.IsNullOrEmpty(q))
            {
                (string GroupKey, string Header)[] sections =
                [
                    ("catalog", "可以下载"),
                    ("custom", "你添加的"),
                    ("pc", "这台电脑"),
                    ("builtin", "自带"),
                ];
                foreach (var (gKey, gHead) in sections)
                {
                    var grpItems = filtered.Where(x => x.GroupKey == gKey).ToArray();
                    if (grpItems.Length == 0) continue;
                    fontListStack.Children.Add(new TextBlock
                    {
                        Text = L10n.T(gHead),
                        FontSize = 10.5,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        Foreground = ThemeHelper.Brush(p.T3),
                        Margin = new Thickness(8, 8, 4, 2),
                    });
                    foreach (var gi in grpItems)
                        AddFontRow(gi);
                }
            }
            else
            {
                foreach (var fi in filtered)
                    AddFontRow(fi);
            }

            var activeItem = allItems.FirstOrDefault(x => string.Equals(x.Id, selectedFontId, StringComparison.OrdinalIgnoreCase)) ?? allItems.FirstOrDefault();
            UpdateSpecimenPane(activeItem);
        }

        applyFontBtn.Click += async (_, _) =>
        {
            var allItems = BuildAllItems();
            var target = allItems.FirstOrDefault(x => string.Equals(x.Id, selectedFontId, StringComparison.OrdinalIgnoreCase));
            if (target is not null)
                await ApplySelectedFontAsync(target);
        };

        importFileBtn.Click += async (_, _) =>
        {
            string? fontPath = await NativePickerHelper.PickSingleFileAsync(this, [".ttf", ".otf", ".ttc", ".woff2"]);
            if (string.IsNullOrEmpty(fontPath)) return;
            try
            {
                string fn = _library.Fonts.ImportFontFile(fontPath);
                string newId = $"file:{fn}";
                PinIfExternal(newId);
                selectedCategory = "all";
                selectedFontId = newId;
                fontDownloadErrors.Remove(newId);
                RebuildFontBookAndTree();
                RenderDynamicPage("reading");
            }
            catch (Exception ex)
            {
                dlErrorTb.Text = ex.Message;
                dlErrorTb.Visibility = Visibility.Visible;
            }
        };

        searchBox.TextChanged += (_, _) =>
        {
            searchQuery = searchBox.Text;
            RebuildFontBookAndTree();
        };

        _fontBookProgressUpdater = () =>
        {
            if (fontBookClosed || _closed) return;
            var allItems = BuildAllItems();
            var cur = allItems.FirstOrDefault(x => string.Equals(x.Id, selectedFontId, StringComparison.OrdinalIgnoreCase));
            UpdateSpecimenPane(cur);
        };
        _activeFontBookRebuild = RebuildFontBookAndTree;

        if (loadingSysFonts)
        {
            _ = _library.Fonts.LoadSystemCjkFontsAsync().ContinueWith(t =>
            {
                if (t.Status == TaskStatus.RanToCompletion)
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (_closed || fontBookClosed) return;
                        sysFonts = t.Result;
                        loadingSysFonts = false;
                        RebuildFontBookAndTree();
                    });
                }
            });
        }

        RebuildFontBook();
        L10n.ApplyTree(rootGrid);

        dlgRef = YYChrome.CreateMacSheet(RootGrid.XamlRoot, IsDark, 800, rootGrid, maxHeight: 540, tint: _library.Prefs.Tint);
        dlgRef.Closed += (_, _) =>
        {
            fontBookClosed = true;
            _fontBookProgressUpdater = null;
            _activeFontBookRebuild = null;
            CancelInFlightFontBookDownloads();
        };
        try
        {
            await dlgRef.ShowSafeAsync(_library.Prefs.Tint);
        }
        finally
        {
            fontBookClosed = true;
            _fontBookProgressUpdater = null;
            _activeFontBookRebuild = null;
            CancelInFlightFontBookDownloads();
        }
    }

    // ---------- 3. 外文（1:1 MacForeignSettings.swift:10-191） ----------

    private void BuildForeignPage()
    {
        var p = CurrentPalette;
        var tint = CurrentTint;

        DynamicPageHost.Children.Add(MakeHeading("查词"));
        var builtInLabel = new TextBlock
        {
            Text = _library.Ecdict.IsAvailable ? "✓ 已就绪" : "未就绪",
            FontSize = 12,
            Foreground = ThemeHelper.Brush(_library.Ecdict.IsAvailable ? p.Ok : p.T3),
        };
        var accentSeg = MakeSegments(
            [("uk", "英音"), ("us", "美音")],
            _library.Prefs.ForeignAccent,
            ac =>
            {
                string prev = _library.Prefs.ForeignAccent;
                if (prev == ac) return;
                _library.Prefs.ForeignAccent = ac;
                _library.SavePrefs();
                RecordPrefChange(
                    nameof(DevicePrefs.ForeignAccent),
                    "英文发音",
                    ac == "uk" ? "英音" : "美音",
                    prev == "uk" ? "英音" : "美音",
                    prev,
                    ac,
                    undo: () => { _library.Prefs.ForeignAccent = prev; _library.SavePrefs(); },
                    redo: () => { _library.Prefs.ForeignAccent = ac; _library.SavePrefs(); });
                RenderDynamicPage("foreign");
            },
            width: 124);

        DynamicPageHost.Children.Add(MakeCard(
            MakeRow("系统词典", "点英文词即查，无需联网", builtInLabel),
            MakeRow("查词时自动发音", null, MakeSwitch(_library.Prefs.ForeignSpeakOnLookup, v =>
            {
                bool prev = !v;
                _library.Prefs.ForeignSpeakOnLookup = v;
                _library.SavePrefs();
                RecordPrefChange(
                    nameof(DevicePrefs.ForeignSpeakOnLookup),
                    "自动发音",
                    v ? "开" : "关",
                    prev ? "开" : "关",
                    prev,
                    v,
                    undo: () => { _library.Prefs.ForeignSpeakOnLookup = prev; _library.SavePrefs(); },
                    redo: () => { _library.Prefs.ForeignSpeakOnLookup = v; _library.SavePrefs(); });
            }), compact: true),
            MakeRow("英文发音", null, accentSeg, compact: true)));

        DynamicPageHost.Children.Add(MakeHeading("MDict 词典"));
        var dicts = _library.GetDictionaries();
        var mdxRows = new List<UIElement>();
        for (int di = 0; di < dicts.Count; di++)
        {
            int curIdx = di;
            var d = dicts[di];
            string did = d.Id;
            string dTitle = d.Title;
            var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            if (dicts.Count > 1)
            {
                var upBtn = new Button { Style = (Style)Application.Current.Resources["YYIconButtonStyle"], Content = "↑", Width = 24, Height = 24, FontSize = 11, IsEnabled = curIdx > 0 };
                upBtn.Click += (_, _) =>
                {
                    var ids = dicts.Select(x => x.Id).ToList();
                    (ids[curIdx - 1], ids[curIdx]) = (ids[curIdx], ids[curIdx - 1]);
                    try { _library.OrderDictionaries(ids.ToArray()); } catch { }
                    RenderDynamicPage("foreign");
                };
                var downBtn = new Button { Style = (Style)Application.Current.Resources["YYIconButtonStyle"], Content = "↓", Width = 24, Height = 24, FontSize = 11, IsEnabled = curIdx < dicts.Count - 1 };
                downBtn.Click += (_, _) =>
                {
                    var ids = dicts.Select(x => x.Id).ToList();
                    (ids[curIdx + 1], ids[curIdx]) = (ids[curIdx], ids[curIdx + 1]);
                    try { _library.OrderDictionaries(ids.ToArray()); } catch { }
                    RenderDynamicPage("foreign");
                };
                right.Children.Add(upBtn);
                right.Children.Add(downBtn);
            }
            right.Children.Add(MakeSwitch(d.Enabled, on => _library.SetDictionaryEnabled(did, on)));
            var rowEl = MakeRow(d.Title, $"{d.Entries:N0} 个词条", right);
            var ctx = new MenuFlyout();
            var rmItem = new MenuFlyoutItem { Text = L10n.T("移除…"), Foreground = ThemeHelper.Brush(p.Danger) };
            rmItem.Click += async (_, _) =>
            {
                bool confirmed = await YYChrome.ShowConfirmAsync(
                    RootGrid.XamlRoot,
                    IsDark,
                    L10n.T($"移除词典「{dTitle}」？"),
                    L10n.T("移除后可随时重新导入 .mdx 文件。"),
                    confirmText: L10n.T("移除"),
                    cancelText: L10n.T("取消"),
                    destructive: true,
                    tint: _library.Prefs.Tint);
                if (confirmed)
                {
                    _library.RemoveDictionary(did);
                    RenderDynamicPage("foreign");
                }
            };
            ctx.Items.Add(rmItem);
            rowEl.ContextFlyout = ctx;
            mdxRows.Add(rowEl);
        }

        var mdxRight = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        var mdxSpinner = new ProgressRing { Width = 14, Height = 14, IsActive = false, Visibility = Visibility.Collapsed, Foreground = ThemeHelper.Brush(tint.Accent) };
        var pickMdxBtn = new Button { Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"], Content = "选择文件…" };
        pickMdxBtn.Click += async (_, _) =>
        {
            string? path = await NativePickerHelper.PickSingleFileAsync(this, [".mdx"]);
            if (string.IsNullOrEmpty(path)) return;
            pickMdxBtn.IsEnabled = false;
            pickMdxBtn.Content = L10n.T("正在导入…");
            mdxSpinner.Visibility = Visibility.Visible;
            mdxSpinner.IsActive = true;
            try
            {
                await Task.Run(() => _library.ImportDictionary(path));
                RenderDynamicPage("foreign");
            }
            catch (Exception ex)
            {
                pickMdxBtn.IsEnabled = true;
                pickMdxBtn.Content = L10n.T("选择文件…");
                mdxSpinner.IsActive = false;
                mdxSpinner.Visibility = Visibility.Collapsed;
                await ShowInfoDialogAsync("词典导入失败", ex.Message);
            }
        };
        mdxRight.Children.Add(mdxSpinner);
        mdxRight.Children.Add(pickMdxBtn);
        mdxRows.Add(MakeRow("导入词典（.mdx）", "按列表顺序优先查词，右键可移除", mdxRight));
        DynamicPageHost.Children.Add(MakeCard(mdxRows.ToArray()));

        static string GlossLevelTitle(byte lv) => lv switch
        {
            2 => "四级",
            3 => "六级",
            4 => "考研",
            5 => "雅思",
            6 => "GRE",
            _ => "六级",
        };

        DynamicPageHost.Children.Add(MakeHeading("难词注释"));
        bool glossOn = _library.Prefs.ForeignGlossEnabled && _library.Prefs.GlossLevel > 0;
        var glossSw = MakeSwitch(glossOn, on =>
        {
            bool prev = _library.Prefs.ForeignGlossEnabled;
            _library.Prefs.ForeignGlossEnabled = on;
            if (on && _library.Prefs.GlossLevel == 0) _library.Prefs.GlossLevel = 3;
            _library.SavePrefs();
            App.CurrentApp.BroadcastForeignPrefsChanged();
            RecordPrefChange(
                nameof(DevicePrefs.ForeignGlossEnabled),
                "难词行间释义",
                on ? "开" : "关",
                prev ? "开" : "关",
                prev,
                on,
                undo: () => { _library.Prefs.ForeignGlossEnabled = prev; _library.SavePrefs(); App.CurrentApp.BroadcastForeignPrefsChanged(); },
                redo: () => { _library.Prefs.ForeignGlossEnabled = on; _library.SavePrefs(); App.CurrentApp.BroadcastForeignPrefsChanged(); });
            RenderDynamicPage("foreign");
            RenderInspector("foreign");
        });
        var lvlSeg = MakeSegments<byte>(
            [(2, "四级"), (3, "六级"), (4, "考研"), (5, "雅思"), (6, "GRE")],
            _library.Prefs.GlossLevel == 0 ? (byte)3 : _library.Prefs.GlossLevel,
            lv =>
            {
                byte prevLv = _library.Prefs.GlossLevel == 0 ? (byte)3 : _library.Prefs.GlossLevel;
                if (prevLv == lv && _library.Prefs.ForeignGlossEnabled) return;
                _library.Prefs.ForeignGlossEnabled = true;
                _library.Prefs.GlossLevel = lv;
                _library.SavePrefs();
                App.CurrentApp.BroadcastForeignPrefsChanged();
                RecordPrefChange(
                    nameof(DevicePrefs.GlossLevel),
                    "词汇难度",
                    GlossLevelTitle(lv),
                    GlossLevelTitle(prevLv),
                    prevLv,
                    lv,
                    undo: () => { _library.Prefs.GlossLevel = prevLv; _library.SavePrefs(); App.CurrentApp.BroadcastForeignPrefsChanged(); },
                    redo: () => { _library.Prefs.GlossLevel = lv; _library.SavePrefs(); App.CurrentApp.BroadcastForeignPrefsChanged(); });
                RenderDynamicPage("foreign");
                RenderInspector("foreign");
            },
            width: 220);
        lvlSeg.IsHitTestVisible = glossOn && !_library.Prefs.GlossVocabOnly;
        lvlSeg.Opacity = glossOn && !_library.Prefs.GlossVocabOnly ? 1.0 : 0.45;

        var vocabOnlySw = MakeSwitch(_library.Prefs.GlossVocabOnly, on =>
        {
            bool prev = !on;
            _library.Prefs.GlossVocabOnly = on;
            _library.SavePrefs();
            App.CurrentApp.BroadcastForeignPrefsChanged();
            RecordPrefChange(
                nameof(DevicePrefs.GlossVocabOnly),
                "仅标注生词本中的词",
                on ? "开" : "关",
                prev ? "开" : "关",
                prev,
                on,
                undo: () => { _library.Prefs.GlossVocabOnly = prev; _library.SavePrefs(); App.CurrentApp.BroadcastForeignPrefsChanged(); },
                redo: () => { _library.Prefs.GlossVocabOnly = on; _library.SavePrefs(); App.CurrentApp.BroadcastForeignPrefsChanged(); });
            RenderDynamicPage("foreign");
            RenderInspector("foreign");
        });
        vocabOnlySw.IsEnabled = glossOn;

        DynamicPageHost.Children.Add(MakeCard(
            MakeRow("难词行间释义", "阅读英文书时，在较难词汇上方标注简短中文词义", glossSw),
            MakeRow("词汇难度", "等级越高，仅标注更生僻的词", lvlSeg),
            MakeRow("仅标注生词本中的词", null, vocabOnlySw, compact: true)));

        DynamicPageHost.Children.Add(MakeHeading("生词"));
        DynamicPageHost.Children.Add(MakeCard(
            MakeRow("在书中标记生词", "「在学」生词在所有书中以虚线标出，标为「已掌握」后不再标记", MakeSwitch(_library.Prefs.ForeignMarkVocab, v =>
            {
                bool prev = !v;
                _library.Prefs.ForeignMarkVocab = v;
                _library.SavePrefs();
                App.CurrentApp.BroadcastForeignPrefsChanged();
                RecordPrefChange(
                    nameof(DevicePrefs.ForeignMarkVocab),
                    "在书中标记生词",
                    v ? "开" : "关",
                    prev ? "开" : "关",
                    prev,
                    v,
                    undo: () => { _library.Prefs.ForeignMarkVocab = prev; _library.SavePrefs(); App.CurrentApp.BroadcastForeignPrefsChanged(); },
                    redo: () => { _library.Prefs.ForeignMarkVocab = v; _library.SavePrefs(); App.CurrentApp.BroadcastForeignPrefsChanged(); });
                RenderInspector("foreign");
            }))));

        // 1:1 MacForeignSettings.swift:58-68：翻译卡片
        DynamicPageHost.Children.Add(MakeHeading("翻译"));
        var trStatus = new TextBlock
        {
            Text = _library.Companion.IsConfigured ? "✓ 已就绪" : "按需连接书伴或本机 Ollama",
            FontSize = 11.5,
            Foreground = ThemeHelper.Brush(_library.Companion.IsConfigured ? p.Ok : p.T3),
        };
        DynamicPageHost.Children.Add(MakeCard(
            MakeRow("系统与本地翻译", "本地 Ollama 离线运行不上传原文；供整页译文与双击整句翻译使用", trStatus)));
    }

    // ---------- 4. 朗读（1:1 MacListeningSettings.swift & MacVoicePicker.swift:1-2114） ----------

    private sealed record VoiceCatalogItem(
        string Id,
        string Name,
        string Subtitle,
        string SourceKey,
        string SourceName,
        string Lang, // "zh" | "en" | "bi"
        string SectionTitle);

    private static (Color Bg, Color Fg) VoiceColorsOf(string voiceId, bool dark)
    {
        (Color LightBg, Color LightFg, Color DarkBg, Color DarkFg)[] pairs =
        [
            (Color.FromArgb(255, 250, 232, 224), Color.FromArgb(255, 166, 75, 53), Color.FromArgb(64, 196, 98, 74), Color.FromArgb(255, 244, 172, 152)),
            (Color.FromArgb(255, 225, 239, 230), Color.FromArgb(255, 54, 112, 75), Color.FromArgb(64, 72, 142, 98), Color.FromArgb(255, 162, 222, 182)),
            (Color.FromArgb(255, 224, 236, 248), Color.FromArgb(255, 52, 98, 150), Color.FromArgb(64, 72, 126, 186), Color.FromArgb(255, 166, 206, 246)),
            (Color.FromArgb(255, 247, 235, 212), Color.FromArgb(255, 148, 98, 32), Color.FromArgb(64, 184, 128, 48), Color.FromArgb(255, 242, 204, 142)),
            (Color.FromArgb(255, 238, 228, 246), Color.FromArgb(255, 112, 72, 152), Color.FromArgb(64, 142, 96, 188), Color.FromArgb(255, 208, 180, 242)),
            (Color.FromArgb(255, 222, 241, 240), Color.FromArgb(255, 44, 116, 116), Color.FromArgb(64, 62, 148, 148), Color.FromArgb(255, 156, 224, 222)),
            (Color.FromArgb(255, 248, 228, 236), Color.FromArgb(255, 156, 66, 102), Color.FromArgb(64, 188, 86, 126), Color.FromArgb(255, 242, 174, 200)),
            (Color.FromArgb(255, 236, 238, 222), Color.FromArgb(255, 96, 108, 48), Color.FromArgb(64, 124, 138, 66), Color.FromArgb(255, 202, 214, 154)),
        ];
        uint hash = 2166136261u;
        foreach (char c in voiceId)
            hash = (hash ^ c) * 16777619u;
        var p = pairs[hash % (uint)pairs.Length];
        return dark ? (p.DarkBg, p.DarkFg) : (p.LightBg, p.LightFg);
    }

    private static double[] DeterministicWaveHeights(string seed, int count, double minH = 4.0, double maxH = 17.0)
    {
        var arr = new double[count];
        uint s = 2166136261u;
        foreach (char c in seed)
            s = (s ^ c) * 16777619u;
        for (int i = 0; i < count; i++)
        {
            s = s * 1664525u + 1013904223u;
            double unit = ((s >> 8) & 0xFFFF) / 65535.0;
            double env = Math.Sin(Math.PI * (i + 0.5) / count);
            arr[i] = Math.Round(minH + (maxH - minH) * (0.25 + 0.75 * env) * unit, 1);
        }
        return arr;
    }

    private static string CleanVoiceShortName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "默认旁白";
        int idx = 0;
        while (idx < raw.Length && !char.IsLetterOrDigit(raw[idx]) && raw[idx] < 0x4E00) idx++;
        string s = idx < raw.Length ? raw[idx..].Trim() : raw.Trim();
        int sep = s.IndexOf(" · ", StringComparison.Ordinal);
        if (sep > 0) s = s[..sep].Trim();
        int paren = s.IndexOfAny(['(', '（']);
        if (paren > 1) s = s[..paren].Trim();
        return string.IsNullOrEmpty(s) ? raw.Trim() : s;
    }

    private string InferSourceKeyForVoiceId(string? vid)
    {
        if (string.IsNullOrWhiteSpace(vid)) return "kokoro";
        if (vid.StartsWith("kokoro:", StringComparison.OrdinalIgnoreCase)
            || vid.StartsWith("kokoro.", StringComparison.OrdinalIgnoreCase)
            || vid.StartsWith("card.", StringComparison.OrdinalIgnoreCase)
            || vid.StartsWith("cloned.", StringComparison.OrdinalIgnoreCase))
            return "kokoro";
        if (vid.StartsWith("cloud.", StringComparison.OrdinalIgnoreCase))
        {
            string rest = vid[6..];
            int dot = rest.IndexOf('.');
            if (dot > 0) return rest[..dot].ToLowerInvariant();
        }
        return "pc";
    }

    private string SourceTitleOfKey(string srcKey) => srcKey switch
    {
        "kokoro" => "语阅离线语音 (Kokoro)",
        "edge" => "微软 Edge",
        "free" => "在线自然语音",
        "pc" => "这台电脑 · 系统语音",
        _ when CloudVoiceEntry.TryParseProviderKey(srcKey, out var cp) => CloudVoiceService.ProviderDisplayName(cp, _library.Prefs),
        _ => "语阅离线语音",
    };

    private string SourceShortTabTitle(string srcKey) => srcKey switch
    {
        "kokoro" => "离线语音",
        "edge" => "微软 Edge",
        "free" => "在线语音",
        "pc" => "系统语音",
        _ when CloudVoiceEntry.TryParseProviderKey(srcKey, out var cp) => CloudVoiceService.ProviderDisplayName(cp, _library.Prefs),
        _ => srcKey,
    };

    private string SourceCostLabel(string srcKey) => srcKey switch
    {
        "kokoro" => "免费 · 不联网",
        "edge" => "免 Key 免费 · 微软 Edge 神经语音",
        "free" => "免 Key 免费 · 已读句子缓存在本机",
        "pc" => "免费 · 不联网（机械合成音）",
        "xiaomi" => "限时公测免费 · 按服务商规则",
        _ when CloudVoiceEntry.TryParseProviderKey(srcKey, out var cp) && CloudVoiceService.IsLocalProvider(cp) => "免费 · 本机或局域网运行",
        _ => "按服务商计费 · 已读句子缓存在本机",
    };

    private List<string> GetActiveVoiceSourceTabs()
    {
        var list = new List<string> { "kokoro" };
        if (_library.CloudVoices.IsConfigured(CloudTtsProvider.Edge) || _focusedVoiceSource == "edge")
            list.Add("edge");
        if (_library.CloudVoices.IsConfigured(CloudTtsProvider.Free) || _focusedVoiceSource == "free")
            list.Add("free");
        foreach (var prov in CloudVoiceService.AllProviders.Where(x => x is not CloudTtsProvider.Edge and not CloudTtsProvider.Free))
        {
            string pk = CloudVoiceEntry.ProviderKey(prov);
            if (_library.CloudVoices.IsConfigured(prov) || _library.CloudVoices.IsAutoDiscovered(prov) || _focusedVoiceSource == pk)
                list.Add(pk);
        }
        list.Add("pc");
        return list;
    }

    private List<VoiceCatalogItem> BuildVoicesForSource(string srcKey, string bookLang)
    {
        var items = new List<VoiceCatalogItem>();
        bool wantEn = bookLang == "en";

        if (srcKey == "cloned")
        {
            foreach (var cloned in _library.LiveVoice.GetClonedCards())
            {
                int styles = Math.Max(0, cloned.Clips.Length - 1);
                string sub = styles > 0 ? $"本机克隆声音卡 · 已录 {styles}/6 种情绪" : "本机克隆声音卡 · 中英双语";
                items.Add(new VoiceCatalogItem(
                    cloned.Id,
                    cloned.Name,
                    sub,
                    "kokoro",
                    "我的克隆声音",
                    "bi",
                    "我的克隆"));
            }
            return items;
        }

        if (srcKey == "kokoro")
        {
            var ko = _library.Kokoro;
            var speakers = ko.GetAllSpeakers();
            var primary = speakers.Where(s => wantEn
                ? s.Lang.StartsWith("en", StringComparison.OrdinalIgnoreCase)
                : s.Lang.StartsWith("zh", StringComparison.OrdinalIgnoreCase)).ToArray();
            var secondary = speakers.Except(primary).ToArray();

            foreach (var sp in primary)
            {
                items.Add(new VoiceCatalogItem(
                    sp.Id,
                    CleanVoiceShortName(sp.Name),
                    sp.Summary,
                    "kokoro",
                    "语阅离线语音 (Kokoro)",
                    wantEn ? "en" : "zh",
                    "Kokoro 轻量离线 · 推荐"));
            }

            foreach (var cloned in _library.LiveVoice.GetClonedCards())
            {
                int styles = Math.Max(0, cloned.Clips.Length - 1);
                string sub = styles > 0 ? $"本机克隆声音卡 · 已录 {styles}/6 种情绪" : "本机克隆声音卡 · 中英双语";
                items.Add(new VoiceCatalogItem(
                    cloned.Id,
                    cloned.Name,
                    sub,
                    "kokoro",
                    "我的克隆声音",
                    "bi",
                    "我的克隆"));
            }

            if (_library.LiveVoice.IsReady)
            {
                foreach (var card in _library.LiveVoice.GetBuiltinCards())
                {
                    items.Add(new VoiceCatalogItem(
                        card.Id,
                        CleanVoiceShortName(card.Name),
                        "24kHz 离线精做角色人声 · 中英双语",
                        "kokoro",
                        "语阅精做声音",
                        "bi",
                        "精做角色声音"));
                }
            }

            string otherSec = wantEn ? "中文音色（也可用于英文混排）" : "English Narrators";
            foreach (var sp in secondary)
            {
                items.Add(new VoiceCatalogItem(
                    sp.Id,
                    CleanVoiceShortName(sp.Name),
                    sp.Summary,
                    "kokoro",
                    "语阅离线语音 (Kokoro)",
                    wantEn ? "zh" : "en",
                    otherSec));
            }
            return items;
        }

        if (srcKey == "pc")
        {
            var allOpts = AudioPlayer.GetAllVoiceOptions(_library)
                .Where(o => InferSourceKeyForVoiceId(o.Id) == "pc")
                .ToArray();
            foreach (var o in allOpts)
            {
                bool isEnOpt = o.Label.Contains("English", StringComparison.OrdinalIgnoreCase)
                    || o.Label.Contains("Zira", StringComparison.OrdinalIgnoreCase)
                    || o.Label.Contains("David", StringComparison.OrdinalIgnoreCase)
                    || o.Label.Contains("Mark", StringComparison.OrdinalIgnoreCase);
                items.Add(new VoiceCatalogItem(
                    o.Id,
                    CleanVoiceShortName(o.Label),
                    "不推荐 · 系统自带机械合成音",
                    "pc",
                    "这台电脑 · 系统语音",
                    isEnOpt ? "en" : "zh",
                    isEnOpt == wantEn ? "系统合成音 · 不推荐" : "其他语种系统音"));
            }
            if (items.Count == 0)
                items.Add(new VoiceCatalogItem("sys:default", "系统默认语音", "不推荐 · 系统自带机械合成音", "pc", "这台电脑 · 系统语音", "bi", "系统合成音 · 不推荐"));
            return items.OrderByDescending(x => x.Lang == (wantEn ? "en" : "zh") || x.Lang == "bi").ToList();
        }

        if (CloudVoiceEntry.TryParseProviderKey(srcKey, out var prov))
        {
            string pTitle = SourceTitleOfKey(srcKey);
            var voices = _library.CloudVoices.GetVoices(prov);
            var matchGroup = voices.Where(v =>
                string.IsNullOrEmpty(v.Language)
                || v.Language.Equals("multi", StringComparison.OrdinalIgnoreCase)
                || v.Language.StartsWith(wantEn ? "en" : "zh", StringComparison.OrdinalIgnoreCase)).ToArray();
            var restGroup = voices.Except(matchGroup).ToArray();

            string mainHeader = $"{pTitle} · {(wantEn ? "English" : "中文")} ({matchGroup.Length})";
            foreach (var cv in matchGroup)
            {
                items.Add(new VoiceCatalogItem(
                    cv.FullId,
                    CleanVoiceShortName(cv.Name),
                    string.IsNullOrWhiteSpace(cv.Age) ? pTitle : cv.Age,
                    srcKey,
                    pTitle,
                    wantEn ? "en" : "zh",
                    mainHeader));
            }
            if (restGroup.Length > 0)
            {
                string subHeader = wantEn ? "其他语种音色" : "English & Multilingual";
                foreach (var cv in restGroup)
                {
                    items.Add(new VoiceCatalogItem(
                        cv.FullId,
                        CleanVoiceShortName(cv.Name),
                        string.IsNullOrWhiteSpace(cv.Age) ? pTitle : cv.Age,
                        srcKey,
                        pTitle,
                        wantEn ? "zh" : "en",
                        subHeader));
                }
            }
        }
        return items;
    }

    private VoiceCatalogItem ResolveVoiceItemById(string? voiceId, string fallbackLang)
    {
        if (!string.IsNullOrWhiteSpace(voiceId))
        {
            string src = InferSourceKeyForVoiceId(voiceId);
            var srcVoices = BuildVoicesForSource(src, fallbackLang);
            var hit = srcVoices.FirstOrDefault(v =>
                string.Equals(v.Id, voiceId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(v.Id, "sys:" + voiceId, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;

            var opt = AudioPlayer.GetAllVoiceOptions(_library)
                .FirstOrDefault(o => o.Id == voiceId || o.Id == "sys:" + voiceId);
            if (opt is not null)
            {
                return new VoiceCatalogItem(
                    opt.Id,
                    CleanVoiceShortName(opt.Label),
                    SourceTitleOfKey(src),
                    src,
                    SourceTitleOfKey(src),
                    fallbackLang,
                    "");
            }
        }

        var koDefaults = BuildVoicesForSource("kokoro", fallbackLang);
        if (koDefaults.Count > 0) return koDefaults[0];
        return new VoiceCatalogItem("kokoro:zf_001", "知声", "温润自然旁白", "kokoro", "语阅离线语音 (Kokoro)", fallbackLang, "");
    }

    private string GetActiveVoiceIdForLang(string lang)
    {
        if (lang == "en")
        {
            if (!string.IsNullOrWhiteSpace(_library.Prefs.VoiceIdEn))
                return _library.Prefs.VoiceIdEn;
            return _library.Prefs.GetVoiceForLanguage("en");
        }
        return _library.Prefs.VoiceId;
    }

    private string GetActiveVoiceSampleLine(string lang)
    {
        if (_customVoiceLines.TryGetValue(lang, out var custom) && !string.IsNullOrWhiteSpace(custom))
            return custom.Trim();
        var q = DailyQuote.Current(_library, lang);
        if (!string.IsNullOrWhiteSpace(q.Text)) return q.Text;
        return lang == "en"
            ? "I declare after all there is no enjoyment like reading!"
            : "江上之清风，与山间之明月，耳得之而为声，目遇之而成色。";
    }

    private static int EstimateSpeakSeconds(string text, double rate)
    {
        int chars = Math.Max(4, text.Trim().Length);
        double raw = (chars / 4.2) / Math.Max(0.8, rate);
        return Math.Clamp((int)Math.Round(raw), 2, 18);
    }

    private UIElement MakeVoiceAvatar(string voiceId, string name, double size, bool pinned = false)
    {
        var (bg, fg) = VoiceColorsOf(voiceId, IsDark);
        string ch = "声";
        foreach (char c in name.Trim())
        {
            if (char.IsLetterOrDigit(c) || c >= 0x4E00)
            {
                ch = c.ToString().ToUpperInvariant();
                break;
            }
        }

        var circle = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2.0),
            Background = ThemeHelper.Brush(bg),
            Child = new TextBlock
            {
                Text = ch,
                FontSize = Math.Round(size * 0.42, 1),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = ThemeHelper.Brush(fg),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        if (!pinned) return circle;

        var wrap = new Grid { Width = size, Height = size };
        wrap.Children.Add(circle);
        wrap.Children.Add(new Border
        {
            Width = 13,
            Height = 13,
            CornerRadius = new CornerRadius(6.5),
            Background = ThemeHelper.Brush(CurrentPalette.Card),
            BorderBrush = ThemeHelper.Brush(CurrentPalette.Line),
            BorderThickness = new Thickness(0.5),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, -2, -2),
            Child = YYIcons.Make("pin", 7.5, ThemeHelper.Brush(fg), filled: true),
        });
        return wrap;
    }

    private bool IsKokoroVoiceQueuedForLang(VoiceCatalogItem item, string lang)
    {
        if (_library.Kokoro.IsInstalled || !item.Id.StartsWith("kokoro", StringComparison.OrdinalIgnoreCase))
            return false;
        if (_queuedKokoroVoiceIds.TryGetValue(lang, out var qid) &&
            string.Equals(qid, item.Id, StringComparison.OrdinalIgnoreCase))
            return true;
        string? pendingName = _library.Kokoro.GetPendingVoiceName(lang);
        if (!string.IsNullOrEmpty(pendingName))
        {
            var sp = _library.Kokoro.FindSpeaker(item.Id);
            if (sp is not null && string.Equals(sp.Name, pendingName, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private void SelectVoiceForBookLang(VoiceCatalogItem item, string lang)
    {
        if (item.SourceKey == "kokoro"
            && item.Id.StartsWith("kokoro", StringComparison.OrdinalIgnoreCase)
            && !_library.Kokoro.IsInstalled)
        {
            _queuedKokoroVoiceIds[lang] = item.Id;
            _library.Kokoro.QueueSelection(item.Id, lang);
            ShowFlashBanner(L10n.T($"开始下载 Kokoro，下好后自动启用「{item.Name}」"));
            if (_activePage == "listening")
            {
                RenderDynamicPage("listening");
                RenderInspector("listening");
            }
            return;
        }

        _queuedKokoroVoiceIds.Remove(lang);
        string oldId = lang == "en" ? _library.Prefs.VoiceIdEn : _library.Prefs.VoiceId;
        var oldItem = ResolveVoiceItemById(oldId, lang);
        if (string.Equals(oldId, item.Id, StringComparison.OrdinalIgnoreCase))
            return;

        if (lang == "en")
        {
            _library.Prefs.VoiceIdEn = item.Id;
            _library.SavePrefs();
            RecordPrefChange(
                nameof(DevicePrefs.VoiceIdEn),
                "英文书声音",
                item.Name,
                oldItem.Name,
                oldId,
                item.Id,
                undo: () => { _library.Prefs.VoiceIdEn = oldId; _library.SavePrefs(); App.CurrentApp.BroadcastVoiceChanged(); },
                redo: () => { _library.Prefs.VoiceIdEn = item.Id; _library.SavePrefs(); App.CurrentApp.BroadcastVoiceChanged(); },
                pageOverride: "listening");
        }
        else
        {
            _library.Prefs.VoiceId = item.Id;
            _library.SavePrefs();
            RecordPrefChange(
                nameof(DevicePrefs.VoiceId),
                "中文书声音",
                item.Name,
                oldItem.Name,
                oldId,
                item.Id,
                undo: () => { _library.Prefs.VoiceId = oldId; _library.SavePrefs(); App.CurrentApp.BroadcastVoiceChanged(); },
                redo: () => { _library.Prefs.VoiceId = item.Id; _library.SavePrefs(); App.CurrentApp.BroadcastVoiceChanged(); },
                pageOverride: "listening");
        }

        _recentVoiceSwap = (lang, oldItem.Id, oldItem.Name, item.Name, DateTimeOffset.Now);
        _selectedInspectorVoiceId = item.Id;
        App.CurrentApp.BroadcastVoiceChanged();
        RenderDynamicPage("listening");
        RenderInspector("listening");
    }

    private async Task StartVoiceAuditionAsync(VoiceCatalogItem item, string lineText, bool forceRestart = false)
    {
        string fallbackLang = _voiceBookLang == "en" ? "en" : "zh";
        string effectiveText = string.IsNullOrWhiteSpace(lineText)
            ? GetActiveVoiceSampleLine(fallbackLang)
            : lineText.Trim();

        if (!forceRestart
            && string.Equals(_auditioningVoiceId, item.Id, StringComparison.OrdinalIgnoreCase)
            && string.Equals(_auditioningLineText, effectiveText, StringComparison.Ordinal))
        {
            StopVoicePreviewUi();
            AudioPlayer.StopActivePreview();
            if (_activePage == "listening")
            {
                RenderDynamicPage("listening");
                RenderInspector("listening");
            }
            return;
        }

        if (item.SourceKey == "kokoro"
            && item.Id.StartsWith("kokoro", StringComparison.OrdinalIgnoreCase)
            && !_library.Kokoro.IsInstalled)
        {
            if (_library.Kokoro.State != KokoroStateKind.Downloading)
                _ = _library.Kokoro.DownloadAsync();
            ShowFlashBanner(L10n.T("正在下载 Kokoro 轻量离线语音…"));
            return;
        }

        if (item.Id.StartsWith("cloned.", StringComparison.OrdinalIgnoreCase)
            && !_library.Prefs.ClonedUseElevenLabs.Contains(item.Id)
            && !_library.LiveVoice.IsReady)
        {
            _selectedInspectorVoiceId = item.Id;
            if (_library.LiveVoice.State != LiveVoiceStateKind.Downloading)
                _ = _library.LiveVoice.DownloadAsync();
            ShowFlashBanner(L10n.T("正在下载 160 MB 本地克隆语音引擎，下好后即可朗读任意句子…"));
            if (_activePage == "listening")
            {
                RenderDynamicPage("listening");
                RenderInspector("listening");
            }
            return;
        }

        StopVoicePreviewUi();
        AudioPlayer.StopActivePreview();

        _heardVoiceIds.Add(item.Id);
        _selectedInspectorVoiceId = item.Id;
        _auditioningVoiceId = item.Id;
        _auditioningLineText = effectiveText;
        _auditionStartAt = DateTimeOffset.Now;
        _auditionEstimatedSecs = Math.Max(1.8, EstimateSpeakSeconds(effectiveText, _library.Prefs.SpeechRate));
        string capturedId = item.Id;

        if (_activePage == "listening")
        {
            RenderDynamicPage("listening");
            RenderInspector("listening");
        }

        try
        {
            await AudioPlayer.PreviewVoiceAsync(_library, item.Id, _library.Prefs.SpeechRate, effectiveText);
        }
        catch (Exception ex)
        {
            if (!_closed && _activePage == "listening")
                ShowFlashBanner(ex.Message);
        }
        finally
        {
            if (!_closed
                && string.Equals(_auditioningVoiceId, capturedId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(_auditioningLineText, effectiveText, StringComparison.Ordinal))
            {
                StopVoicePreviewUi();
                if (_activePage == "listening")
                {
                    RenderDynamicPage("listening");
                    RenderInspector("listening");
                }
            }
        }
    }

    private async Task EnsureVoiceDailyQuoteAsync(string lang, TextBlock? quoteBodyTb = null, TextBlock? quoteHeaderTb = null)
    {
        string before = DailyQuote.Current(_library, lang).Text;
        var updated = await DailyQuote.EnsureTodayAsync(_library, lang);
        if (updated.Text != before)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_closed) return;
                if (quoteBodyTb is not null) quoteBodyTb.Text = updated.Text;
                if (quoteHeaderTb is not null)
                    quoteHeaderTb.Text = string.IsNullOrEmpty(updated.Source) ? L10n.T("每日一言") : $"{L10n.T("每日一言")} · {updated.Source}";
                if (_activePage == "listening" && !_customVoiceLines.ContainsKey(lang))
                    RenderInspector("listening");
            });
        }
    }

    private void BuildListeningPage()
    {
        if (!_localVoiceProbeStarted)
        {
            _localVoiceProbeStarted = true;
            _ = Task.Run(async () =>
            {
                bool changed = await LocalVoiceProbe.Shared.ScanAllAsync(_library);
                if (changed)
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (_closed) return;
                        if (_activePage == "listening")
                        {
                            RenderDynamicPage("listening");
                            RenderInspector("listening");
                        }
                    });
                }
            });
        }

        if (!string.IsNullOrEmpty(_focusedVoiceSource))
        {
            BuildVoiceSourcePage();
            return;
        }

        // Clean Overview Page (1:1 MacVoicePicker.swift:9-98: ONLY 声音来源 + 语速!)
        var p = CurrentPalette;
        var tint = CurrentTint;

        DynamicPageHost.Children.Add(MakeHeading("声音来源", "点一个，进去听它的声音"));

        var srcStack = new StackPanel { Spacing = 2, Padding = new Thickness(4) };
        var curZh = ResolveVoiceItemById(GetActiveVoiceIdForLang("zh"), "zh");
        var curEn = ResolveVoiceItemById(GetActiveVoiceIdForLang("en"), "en");

        (string? Status, bool Pinned) PinnedStatusForSource(string srcKey, string? readyFallback = null)
        {
            bool zhHere = string.Equals(curZh.SourceKey, srcKey, StringComparison.OrdinalIgnoreCase);
            bool enHere = string.Equals(curEn.SourceKey, srcKey, StringComparison.OrdinalIgnoreCase);
            if (zhHere && enHere)
            {
                return string.Equals(curZh.Id, curEn.Id, StringComparison.OrdinalIgnoreCase)
                    ? ($"中英 · {curZh.Name}", true)
                    : ($"中 {curZh.Name} · 英 {curEn.Name}", true);
            }
            if (zhHere) return ($"中文书 · {curZh.Name}", true);
            if (enHere) return ($"英文书 · {curEn.Name}", true);
            return (readyFallback, false);
        }

        TextBlock? koSubTb = null;
        TextBlock? koStatusTb = null;

        void AddSourceRow(string key, string tileKey, string title, string sub, string? status, bool connected, bool pinned, string? tagBadge = null, Color? tagCol = null, bool addDivider = true, Action? customClick = null, bool trackKokoro = false)
        {
            if (addDivider && srcStack.Children.Count > 0)
                srcStack.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(14, 0, 14, 0) });

            var g = new Grid { ColumnSpacing = 12, MinHeight = 52, Padding = new Thickness(12, 8, 12, 8) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(YYChrome.MakeServiceTile(tileKey, IsDark, 34));

            var mid = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            var titleLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            titleLine.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(customClick is not null ? tint.Accent : p.T1) });
            if (!string.IsNullOrEmpty(tagBadge))
                titleLine.Children.Add(MakeStatusPill(tagBadge, tagCol ?? p.Ok));
            mid.Children.Add(titleLine);
            var subTb = new TextBlock { Text = sub, FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis };
            if (trackKokoro) koSubTb = subTb;
            mid.Children.Add(subTb);
            Grid.SetColumn(mid, 1);
            g.Children.Add(mid);

            var stWrap = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            if (pinned && !string.IsNullOrEmpty(status))
                stWrap.Children.Add(YYIcons.Make("pin", 10, ThemeHelper.Brush(p.T3), filled: true));
            if (!string.IsNullOrEmpty(status) || trackKokoro)
            {
                var stTb = new TextBlock
                {
                    Text = status ?? "",
                    Visibility = string.IsNullOrEmpty(status) ? Visibility.Collapsed : Visibility.Visible,
                    FontSize = 11,
                    Foreground = ThemeHelper.Brush(connected || pinned ? p.T2 : p.T3),
                    MaxWidth = 160,
                    MaxLines = 1,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                if (trackKokoro) koStatusTb = stTb;
                stWrap.Children.Add(stTb);
            }
            if (customClick is null)
                stWrap.Children.Add(YYIcons.Make("chevron-right", 10, ThemeHelper.Brush(p.T3)));
            Grid.SetColumn(stWrap, 2);
            g.Children.Add(stWrap);

            var btn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(9),
                BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(Colors.Transparent),
                UseSystemFocusVisuals = false,
                Content = g,
            };
            string sk = key;
            btn.Click += (_, _) =>
            {
                if (customClick is not null) { customClick(); return; }
                SetFocusedVoiceSource(sk);
                _voiceSourceConfigOpen = false;
                RenderDynamicPage("listening");
                RenderInspector("listening");
            };
            srcStack.Children.Add(btn);
        }

        var ko = _library.Kokoro;
        int clonedCount = _library.LiveVoice.GetClonedCards().Count;
        string clonedSuffix = clonedCount > 0 ? $" · {clonedCount} 个克隆声音" : "";
        string koSub = ko.IsInstalled
            ? $"{ko.GetAllSpeakers().Count} 个中英自然音色{clonedSuffix} · 免费免联网"
            : (ko.State == KokoroStateKind.Downloading
                ? $"正在下载轻量离线语音 {(int)Math.Round(ko.DownloadProgress * 100)}% · 免费免联网"
                : $"跨端轻量中英自然人声 · 点进去一键下载（约 {KokoroVoiceModel.TotalMb} MB）");
        var (koPinText, koPinned) = PinnedStatusForSource(
            "kokoro",
            ko.IsInstalled ? "已就绪" : (ko.State == KokoroStateKind.Downloading ? $"{(int)Math.Round(ko.DownloadProgress * 100)}%" : null));
        AddSourceRow("kokoro", "kokoro", "语阅离线语音 (Kokoro)", koSub, koPinText, ko.IsInstalled, koPinned, "推荐", p.Ok, addDivider: false, trackKokoro: true);

        _liveVoiceProgressUpdater = () =>
        {
            if (ko.State == KokoroStateKind.Downloading)
            {
                int pct = (int)Math.Round(ko.DownloadProgress * 100);
                if (koSubTb is not null) koSubTb.Text = L10n.T($"正在下载轻量离线语音 {pct}% · 免费免联网");
                if (koStatusTb is not null && !koPinned)
                {
                    koStatusTb.Text = $"{pct}%";
                    koStatusTb.Visibility = Visibility.Visible;
                }
            }
        };

        if (_library.CloudVoices.IsConfigured(CloudTtsProvider.Edge))
        {
            var (edgeStatus, edgePinned) = PinnedStatusForSource("edge");
            AddSourceRow("edge", "edge", "微软 Edge", "22 个微软神经音色 · 免 Key 直连，已读缓存在本机",
                edgeStatus, true, edgePinned, "免 Key", p.Ok, addDivider: true);
        }

        if (_library.CloudVoices.IsConfigured(CloudTtsProvider.Free))
        {
            var (freeStatus, freePinned) = PinnedStatusForSource("free");
            AddSourceRow("free", "online", "在线自然语音", "免 Key 直连中英神经人声 · 开箱即听，已读缓存在本机",
                freeStatus, true, freePinned, "免 Key", p.Ok, addDivider: true);
        }

        foreach (var prov in CloudVoiceService.AllProviders.Where(x => x is not CloudTtsProvider.Edge and not CloudTtsProvider.Free))
        {
            string pk = CloudVoiceEntry.ProviderKey(prov);
            bool cfg = _library.CloudVoices.IsConfigured(prov);
            bool autoLive = _library.CloudVoices.IsAutoDiscovered(prov);
            if (!cfg && !autoLive) continue;
            string fallbackSt = autoLive && !_library.Prefs.CloudVoiceEnabled.Contains(pk) ? "已自动发现" : "已连接";
            var (provStatus, provPinned) = PinnedStatusForSource(pk, fallbackSt);
            AddSourceRow(pk, pk, CloudVoiceService.ProviderDisplayName(prov, _library.Prefs), CloudVoiceService.ProviderHelp(prov), provStatus, cfg, provPinned, CloudVoiceService.ProviderBadge(prov), p.T3, addDivider: true);
        }

        var (pcStatus, pcPinned) = PinnedStatusForSource("pc");
        AddSourceRow("pc", "system", "这台电脑 · 系统语音", "系统自带合成机械音 · 体验较差，仅作断网未下载时的兜底",
            pcStatus, false, pcPinned, "不推荐", Color.FromArgb(255, 183, 121, 31), addDivider: true);

        AddSourceRow("__add__", "add", "+ 添加声音来源", "火山豆包、OpenAI、ElevenLabs、小米 MiMo、本机语音接口…",
            null, false, false, null, null, addDivider: true, customClick: () => _ = ShowAddVoiceSourceDialogAsync());

        DynamicPageHost.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(IsDark ? Color.FromArgb(16, 255, 255, 255) : p.Line),
            BorderThickness = new Thickness(0.5),
            Child = srcStack,
        });
        DynamicPageHost.Children.Add(MakeNote("中英文书可分别选择不同声音来源与旁白；克隆声音与离线角色声线在「语阅离线语音」内开启。"));

        // 语速 card (Minimum = 0.8, real MenuFlyout [0.8x, 1.0x, 1.2x, 1.5x, 2.0x] + RecordPrefChange)
        DynamicPageHost.Children.Add(MakeHeading("语速", "读书窗口里调的也是这个"));
        var speedGrid = new Grid { ColumnSpacing = 10, Padding = new Thickness(16, 12, 16, 12) };
        speedGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        speedGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        speedGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        speedGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        speedGrid.Children.Add(new TextBlock { Text = "慢", FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T2), VerticalAlignment = VerticalAlignment.Center });

        double curRate = Math.Clamp(Math.Round(_library.Prefs.SpeechRate, 1), 0.8, 2.0);
        bool updatingSpeed = false;
        var speedSlider = new Slider { Minimum = 0.8, Maximum = 2.0, StepFrequency = 0.1, Value = curRate };
        AutomationProperties.SetName(speedSlider, L10n.T("语速"));
        var speedBtn = new Button
        {
            Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
            Content = $"{curRate:0.0}× ⇕",
            FontSize = 12.5,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = ThemeHelper.Brush(p.T1),
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(speedBtn, L10n.T("语速预设"));

        void BuildSpeedMenu()
        {
            var speedMenu = new MenuFlyout();
            double activeR = Math.Clamp(Math.Round(_library.Prefs.SpeechRate, 1), 0.8, 2.0);
            double[] presets = [0.8, 1.0, 1.2, 1.5, 2.0];
            foreach (double pr in presets)
            {
                double cap = pr;
                bool on = Math.Abs(activeR - cap) < 0.02;
                var mi = new MenuFlyoutItem { Text = on ? $"✓ {cap:0.0}×" : $"{cap:0.0}×" };
                mi.Click += (_, _) => ApplySpeechRate(cap, syncSlider: true);
                speedMenu.Items.Add(mi);
            }
            speedBtn.Flyout = speedMenu;
        }

        void ApplySpeechRate(double nextRate, bool syncSlider)
        {
            double r = Math.Clamp(Math.Round(nextRate, 1), 0.8, 2.0);
            double prev = Math.Clamp(Math.Round(_library.Prefs.SpeechRate, 1), 0.8, 2.0);
            if (Math.Abs(r - prev) < 0.01) return;
            updatingSpeed = true;
            _library.Prefs.SpeechRate = r;
            _library.SavePrefs();
            speedBtn.Content = $"{r:0.0}× ⇕";
            if (syncSlider) speedSlider.Value = r;
            updatingSpeed = false;
            RecordPrefChange(
                nameof(DevicePrefs.SpeechRate),
                "语速",
                $"{r:0.0}×",
                $"{prev:0.0}×",
                prev,
                r,
                undo: () => { _library.Prefs.SpeechRate = prev; _library.SavePrefs(); App.CurrentApp.BroadcastVoiceChanged(); },
                redo: () => { _library.Prefs.SpeechRate = r; _library.SavePrefs(); App.CurrentApp.BroadcastVoiceChanged(); });
            App.CurrentApp.BroadcastVoiceChanged();
            BuildSpeedMenu();
        }
        BuildSpeedMenu();

        speedSlider.ValueChanged += (_, e) =>
        {
            if (updatingSpeed) return;
            ApplySpeechRate(e.NewValue, syncSlider: false);
        };

        Grid.SetColumn(speedSlider, 1);
        speedGrid.Children.Add(speedSlider);
        var fastTb = new TextBlock { Text = "快", FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T2), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(fastTb, 2);
        speedGrid.Children.Add(fastTb);
        Grid.SetColumn(speedBtn, 3);
        speedGrid.Children.Add(speedBtn);
        DynamicPageHost.Children.Add(MakeCard(speedGrid));
    }

    private void BuildVoiceSourcePage()
    {
        var p = CurrentPalette;
        var tint = CurrentTint;
        string srcKey = _focusedVoiceSource;
        string lang = _voiceBookLang == "en" ? "en" : "zh";

        var pageGrid = new Grid { RowSpacing = 10 };
        pageGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        pageGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        pageGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var topStack = new StackPanel { Spacing = 9 };

        // 1. Top Bar: ‹ Back + Horizontal SourceTabs + ⚙ Config Toggle
        var navBar = new Grid { ColumnSpacing = 8 };
        navBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        navBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        navBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var backBtn = new Button
        {
            Width = 28,
            Height = 28,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(14),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0.75),
            UseSystemFocusVisuals = false,
            Content = YYIcons.Make("chevron-left", 12, ThemeHelper.Brush(p.T1)),
        };
        ToolTipService.SetToolTip(backBtn, L10n.T("返回声音来源"));
        AutomationProperties.SetName(backBtn, L10n.T("返回声音来源"));
        backBtn.Click += (_, _) =>
        {
            SetFocusedVoiceSource("");
            _voiceSourceConfigOpen = false;
            StopVoicePreviewUi();
            AudioPlayer.StopActivePreview();
            RenderDynamicPage("listening");
            RenderInspector("listening");
        };
        navBar.Children.Add(backBtn);

        var tabsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var allTabs = GetActiveVoiceSourceTabs();
        var inlineTabs = allTabs.Take(4).ToList();
        if (!inlineTabs.Contains(srcKey))
        {
            if (inlineTabs.Count >= 4) inlineTabs[3] = srcKey;
            else inlineTabs.Add(srcKey);
        }

        foreach (string tk in inlineTabs)
        {
            bool activeTab = tk == srcKey;
            string tileIconKey = tk == "free" ? "online" : (tk == "pc" ? "system" : tk);
            var tabSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
            tabSp.Children.Add(YYChrome.MakeServiceTile(tileIconKey, IsDark, 18));
            tabSp.Children.Add(new TextBlock
            {
                Text = L10n.T(SourceShortTabTitle(tk)),
                FontSize = 11.5,
                FontWeight = activeTab ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                Foreground = ThemeHelper.Brush(activeTab ? p.T1 : p.T2),
                MaxWidth = 88,
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            });
            var tabBtn = new Button
            {
                Height = 28,
                MinHeight = 28,
                Padding = new Thickness(7, 0, 9, 0),
                CornerRadius = new CornerRadius(9),
                BorderThickness = new Thickness(activeTab ? 0.75 : 0),
                BorderBrush = activeTab ? ThemeHelper.Brush(p.Line) : new SolidColorBrush(Colors.Transparent),
                Background = activeTab ? ThemeHelper.Brush(p.Card) : new SolidColorBrush(Colors.Transparent),
                UseSystemFocusVisuals = false,
                Content = tabSp,
            };
            string capTab = tk;
            tabBtn.Click += (_, _) =>
            {
                if (_focusedVoiceSource == capTab) return;
                SetFocusedVoiceSource(capTab);
                _voiceSourceConfigOpen = false;
                RenderDynamicPage("listening");
                RenderInspector("listening");
            };
            tabsRow.Children.Add(tabBtn);
        }

        var extraTabs = allTabs.Except(inlineTabs).ToArray();
        if (extraTabs.Length > 0)
        {
            var moreMenu = new MenuFlyout();
            foreach (string ext in extraTabs)
            {
                string capExt = ext;
                var mi = new MenuFlyoutItem { Text = L10n.T(SourceTitleOfKey(capExt)) };
                mi.Click += (_, _) =>
                {
                    SetFocusedVoiceSource(capExt);
                    _voiceSourceConfigOpen = false;
                    RenderDynamicPage("listening");
                    RenderInspector("listening");
                };
                moreMenu.Items.Add(mi);
            }
            moreMenu.Items.Add(new MenuFlyoutSeparator());
            var addMi = new MenuFlyoutItem { Text = L10n.T("+ 添加声音来源…") };
            addMi.Click += (_, _) => _ = ShowAddVoiceSourceDialogAsync();
            moreMenu.Items.Add(addMi);

            var moreTabsBtn = new Button
            {
                Width = 26,
                Height = 26,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                UseSystemFocusVisuals = false,
                Content = YYIcons.Make("chevron-down", 10, ThemeHelper.Brush(p.T2)),
                Flyout = moreMenu,
            };
            AutomationProperties.SetName(moreTabsBtn, L10n.T("更多声音来源"));
            tabsRow.Children.Add(moreTabsBtn);
        }
        else
        {
            var addTabBtn = new Button
            {
                Width = 26,
                Height = 26,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                UseSystemFocusVisuals = false,
                Content = YYIcons.Make("plus", 11, ThemeHelper.Brush(p.T2)),
            };
            ToolTipService.SetToolTip(addTabBtn, L10n.T("添加声音来源"));
            AutomationProperties.SetName(addTabBtn, L10n.T("添加声音来源"));
            addTabBtn.Click += (_, _) => _ = ShowAddVoiceSourceDialogAsync();
            tabsRow.Children.Add(addTabBtn);
        }

        var tabsScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Disabled,
            Content = tabsRow,
        };
        Grid.SetColumn(tabsScroll, 1);
        navBar.Children.Add(tabsScroll);

        var gearBtn = new Button
        {
            Width = 28,
            Height = 28,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(14),
            Background = ThemeHelper.Brush(_voiceSourceConfigOpen ? tint.Soft : p.Card),
            BorderBrush = ThemeHelper.Brush(_voiceSourceConfigOpen ? tint.Edge : p.Line),
            BorderThickness = new Thickness(0.75),
            UseSystemFocusVisuals = false,
            Content = YYIcons.Make("sliders", 12.5, ThemeHelper.Brush(_voiceSourceConfigOpen ? tint.Accent : p.T2)),
        };
        ToolTipService.SetToolTip(gearBtn, L10n.T("此声音来源设置"));
        AutomationProperties.SetName(gearBtn, L10n.T("此声音来源设置"));
        gearBtn.Click += (_, _) =>
        {
            _voiceSourceConfigOpen = !_voiceSourceConfigOpen;
            RenderDynamicPage("listening");
            RenderInspector("listening");
        };
        Grid.SetColumn(gearBtn, 2);
        navBar.Children.Add(gearBtn);
        topStack.Children.Add(navBar);

        // 2. Language Switcher: 中文书 / 英文书 segmented tabs (fixes P0-5!)
        var langBar = new Grid { ColumnSpacing = 10 };
        langBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        langBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var bookLangSeg = MakeSegments(
            [("zh", "中文书"), ("en", "英文书")],
            lang,
            nextLang =>
            {
                if (_voiceBookLang == nextLang) return;
                _voiceBookLang = nextLang;
                _selectedInspectorVoiceId = "";
                RenderDynamicPage("listening");
                RenderInspector("listening");
            },
            width: 148);
        langBar.Children.Add(bookLangSeg);
        var hintRight = new TextBlock
        {
            Text = "点气泡试听，点右侧「选择」设为默认",
            FontSize = 11,
            Foreground = ThemeHelper.Brush(p.T3),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(hintRight, 1);
        langBar.Children.Add(hintRight);
        topStack.Children.Add(langBar);

        // 3. PinnedVoice Card for active language
        string pinnedId = GetActiveVoiceIdForLang(lang);
        var pinnedVoice = ResolveVoiceItemById(pinnedId, lang);
        string activeSampleLine = GetActiveVoiceSampleLine(lang);
        int estSec = EstimateSpeakSeconds(activeSampleLine, _library.Prefs.SpeechRate);
        var (pinBg, pinFg) = VoiceColorsOf(pinnedVoice.Id, IsDark);

        var pinGrid = new Grid { ColumnSpacing = 10, Padding = new Thickness(12, 8, 12, 8) };
        pinGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        pinGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        pinGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        pinGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        pinGrid.Children.Add(MakeVoiceAvatar(pinnedVoice.Id, pinnedVoice.Name, 28, pinned: true));
        var pinNameTb = new TextBlock
        {
            Text = pinnedVoice.Name,
            FontSize = 12.5,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = ThemeHelper.Brush(p.T1),
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 110,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(pinNameTb, 1);
        pinGrid.Children.Add(pinNameTb);

        bool pinPlaying = string.Equals(_auditioningVoiceId, pinnedVoice.Id, StringComparison.OrdinalIgnoreCase);
        var miniBubbleInner = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        miniBubbleInner.Children.Add(YYIcons.Make(pinPlaying ? "pause" : "play", 9.5, ThemeHelper.Brush(p.Card)));
        var miniWave = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        foreach (double h in DeterministicWaveHeights(pinnedVoice.Id, 12, 3.5, 12.0))
        {
            miniWave.Children.Add(new Border
            {
                Width = 2,
                Height = h,
                CornerRadius = new CornerRadius(1),
                Background = ThemeHelper.Brush(Color.FromArgb(190, p.Card.R, p.Card.G, p.Card.B)),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        miniBubbleInner.Children.Add(miniWave);
        miniBubbleInner.Children.Add(new TextBlock { Text = $"{estSec}″", FontSize = 10, Foreground = ThemeHelper.Brush(p.Card), Opacity = 0.85, VerticalAlignment = VerticalAlignment.Center });

        var miniBubbleBtn = new Button
        {
            Height = 26,
            MinHeight = 26,
            Padding = new Thickness(9, 0, 9, 0),
            CornerRadius = new CornerRadius(13),
            Background = ThemeHelper.Brush(p.T1),
            BorderThickness = new Thickness(0),
            UseSystemFocusVisuals = false,
            Content = miniBubbleInner,
        };
        miniBubbleBtn.Click += async (_, _) => await StartVoiceAuditionAsync(pinnedVoice, activeSampleLine);
        Grid.SetColumn(miniBubbleBtn, 2);
        pinGrid.Children.Add(miniBubbleBtn);

        string pinSourceSuffix = string.Equals(pinnedVoice.SourceKey, srcKey, StringComparison.OrdinalIgnoreCase)
            ? ""
            : $" · {SourceShortTabTitle(pinnedVoice.SourceKey)}";
        var pinRoleTb = new TextBlock
        {
            Text = L10n.T(lang == "en" ? $"念你的英文书{pinSourceSuffix}" : $"念你的中文书{pinSourceSuffix}"),
            FontSize = 11,
            Foreground = ThemeHelper.Brush(pinFg),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(pinRoleTb, 3);
        pinGrid.Children.Add(pinRoleTb);

        topStack.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(11),
            Background = ThemeHelper.Brush(Color.FromArgb(IsDark ? (byte)42 : (byte)110, pinBg.R, pinBg.G, pinBg.B)),
            BorderBrush = ThemeHelper.Brush(Color.FromArgb(70, pinFg.R, pinFg.G, pinFg.B)),
            BorderThickness = new Thickness(0.75),
            Child = pinGrid,
        });

        // 4. KokoroPrepBanner, LiveVoice 160 MB PrepBanner, or System warning banner
        if (srcKey == "kokoro" && !_library.Kokoro.IsInstalled)
        {
            var ko = _library.Kokoro;
            var prepGrid = new Grid { ColumnSpacing = 10, Padding = new Thickness(12, 9, 12, 9) };
            prepGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            prepGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var prepLeft = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
            var prepTitleTb = new TextBlock
            {
                Text = ko.State == KokoroStateKind.Downloading
                    ? L10n.T($"正在下载 Kokoro 轻量离线语音 {(int)Math.Round(ko.DownloadProgress * 100)}%")
                    : L10n.T($"下载 Kokoro 离线语音包 · 约 {KokoroVoiceModel.TotalMb} MB"),
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = ThemeHelper.Brush(ko.State == KokoroStateKind.Downloading ? tint.Accent : p.T1),
            };
            prepLeft.Children.Add(prepTitleTb);
            bool koHasFailMsg = ko.State == KokoroStateKind.Failed && !string.IsNullOrWhiteSpace(ko.StatusMessage);
            prepLeft.Children.Add(new TextBlock
            {
                Text = koHasFailMsg
                    ? L10n.T(ko.StatusMessage)
                    : L10n.T("16 个中英自然旁白 · 断点续传 · 下载好后永久免联网"),
                FontSize = 10.5,
                Foreground = ThemeHelper.Brush(koHasFailMsg ? p.Danger : p.T3),
                TextWrapping = TextWrapping.Wrap,
            });
            ProgressBar? prepBar = null;
            if (ko.State == KokoroStateKind.Downloading)
            {
                prepBar = new ProgressBar { Minimum = 0, Maximum = 100, Value = Math.Round(ko.DownloadProgress * 100), Height = 4, Margin = new Thickness(0, 2, 0, 0) };
                prepLeft.Children.Add(prepBar);
            }
            prepGrid.Children.Add(prepLeft);

            var prepBtn = new Button
            {
                Style = (Style)Application.Current.Resources[ko.State == KokoroStateKind.Downloading ? "YYPillButtonStyle" : "YYPrimaryPillButtonStyle"],
                Content = L10n.T(ko.State == KokoroStateKind.Downloading ? "取消" : (ko.State == KokoroStateKind.Failed ? "继续下载" : "一键下载")),
                Height = 26,
                MinHeight = 26,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
            };
            prepBtn.Click += (_, _) =>
            {
                if (ko.State == KokoroStateKind.Downloading) ko.CancelDownload();
                else _ = ko.DownloadAsync();
            };
            Grid.SetColumn(prepBtn, 1);
            prepGrid.Children.Add(prepBtn);

            _liveVoiceProgressUpdater += () =>
            {
                if (ko.State == KokoroStateKind.Downloading)
                {
                    int pct = (int)Math.Round(ko.DownloadProgress * 100);
                    prepTitleTb.Text = L10n.T($"正在下载 Kokoro 轻量离线语音 {pct}%");
                    if (prepBar is not null) prepBar.Value = pct;
                }
            };

            topStack.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(11),
                Background = ThemeHelper.Brush(p.Card),
                BorderBrush = ThemeHelper.Brush(koHasFailMsg ? p.Danger : p.Line),
                BorderThickness = new Thickness(0.75),
                Child = prepGrid,
            });
        }
        else if (srcKey == "pc")
        {
            var warnGrid = new Grid { ColumnSpacing = 10, Padding = new Thickness(12, 8, 12, 8) };
            warnGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            warnGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            warnGrid.Children.Add(new TextBlock
            {
                Text = "不推荐 · 系统合成机械感较重，仅作断网兜底",
                FontSize = 11.5,
                Foreground = ThemeHelper.Brush(Color.FromArgb(255, 183, 121, 31)),
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
            });
            var switchKoBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
                Content = "换离线语音 ›",
                Height = 24,
                MinHeight = 24,
                FontSize = 11,
            };
            switchKoBtn.Click += (_, _) =>
            {
                SetFocusedVoiceSource("kokoro");
                _voiceSourceConfigOpen = false;
                RenderDynamicPage("listening");
                RenderInspector("listening");
            };
            Grid.SetColumn(switchKoBtn, 1);
            warnGrid.Children.Add(switchKoBtn);
            topStack.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(10),
                Background = ThemeHelper.Brush(Color.FromArgb(IsDark ? (byte)32 : (byte)24, 210, 145, 32)),
                BorderBrush = ThemeHelper.Brush(Color.FromArgb(64, 210, 145, 32)),
                BorderThickness = new Thickness(0.75),
                Child = warnGrid,
            });
        }

        bool hasLocalClonedVoice = _library.LiveVoice.GetClonedCards()
            .Any(c => !_library.Prefs.ClonedUseElevenLabs.Contains(c.Id));
        bool focusedLocalClone =
            (pinnedVoice.Id.StartsWith("cloned.", StringComparison.OrdinalIgnoreCase) && !_library.Prefs.ClonedUseElevenLabs.Contains(pinnedVoice.Id)) ||
            (_selectedInspectorVoiceId.StartsWith("cloned.", StringComparison.OrdinalIgnoreCase) && !_library.Prefs.ClonedUseElevenLabs.Contains(_selectedInspectorVoiceId));
        if ((srcKey is "kokoro" or "cloned")
            && !_library.LiveVoice.IsReady
            && (srcKey == "cloned"
                || hasLocalClonedVoice
                || focusedLocalClone
                || _library.LiveVoice.State is LiveVoiceStateKind.Downloading or LiveVoiceStateKind.Failed))
        {
            var lv = _library.LiveVoice;
            var lvPrepGrid = new Grid { ColumnSpacing = 10, Padding = new Thickness(12, 9, 12, 9) };
            lvPrepGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            lvPrepGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var lvPrepLeft = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
            var lvPrepTitleTb = new TextBlock
            {
                Text = lv.State == LiveVoiceStateKind.Downloading
                    ? L10n.T($"正在下载本地克隆语音引擎 {(int)Math.Round(lv.DownloadProgress * 100)}%")
                    : L10n.T("下载本地克隆语音引擎 · 约 160 MB"),
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = ThemeHelper.Brush(lv.State == LiveVoiceStateKind.Downloading ? tint.Accent : p.T1),
            };
            lvPrepLeft.Children.Add(lvPrepTitleTb);
            bool lvHasFailMsg = lv.State == LiveVoiceStateKind.Failed && !string.IsNullOrWhiteSpace(lv.StatusMessage);
            lvPrepLeft.Children.Add(new TextBlock
            {
                Text = lvHasFailMsg
                    ? L10n.T(lv.StatusMessage)
                    : L10n.T("本地克隆声音合成新句子所需 · 24kHz 离线运行，声音不出本机"),
                FontSize = 10.5,
                Foreground = ThemeHelper.Brush(lvHasFailMsg ? p.Danger : p.T3),
                TextWrapping = TextWrapping.Wrap,
            });
            ProgressBar? lvPrepBar = null;
            if (lv.State == LiveVoiceStateKind.Downloading)
            {
                lvPrepBar = new ProgressBar { Minimum = 0, Maximum = 100, Value = Math.Round(lv.DownloadProgress * 100), Height = 4, Margin = new Thickness(0, 2, 0, 0) };
                lvPrepLeft.Children.Add(lvPrepBar);
            }
            lvPrepGrid.Children.Add(lvPrepLeft);

            var lvPrepBtn = new Button
            {
                Style = (Style)Application.Current.Resources[lv.State == LiveVoiceStateKind.Downloading ? "YYPillButtonStyle" : "YYPrimaryPillButtonStyle"],
                Content = L10n.T(lv.State == LiveVoiceStateKind.Downloading ? "取消" : (lv.State == LiveVoiceStateKind.Failed ? "继续下载" : "一键下载")),
                Height = 26,
                MinHeight = 26,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
            };
            lvPrepBtn.Click += (_, _) =>
            {
                if (lv.State == LiveVoiceStateKind.Downloading) lv.CancelDownload();
                else _ = lv.DownloadAsync();
            };
            Grid.SetColumn(lvPrepBtn, 1);
            lvPrepGrid.Children.Add(lvPrepBtn);

            _liveVoiceProgressUpdater += () =>
            {
                if (lv.State == LiveVoiceStateKind.Downloading)
                {
                    int pct = (int)Math.Round(lv.DownloadProgress * 100);
                    lvPrepTitleTb.Text = L10n.T($"正在下载本地克隆语音引擎 {pct}%");
                    if (lvPrepBar is not null) lvPrepBar.Value = pct;
                }
            };

            topStack.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(11),
                Background = ThemeHelper.Brush(p.Card),
                BorderBrush = ThemeHelper.Brush(lvHasFailMsg ? p.Danger : p.Line),
                BorderThickness = new Thickness(0.75),
                Child = lvPrepGrid,
            });
        }

        // 5. DailyQuoteCard or Custom Audition Line Bubble
        if (_customVoiceLines.TryGetValue(lang, out var userLine) && !string.IsNullOrWhiteSpace(userLine))
        {
            var yourRow = new Grid { ColumnSpacing = 8 };
            yourRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            yourRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var yourLeft = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
            var resetQuoteBtn = new Button
            {
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(4, 2, 4, 2),
                FontSize = 10.5,
                Foreground = ThemeHelper.Brush(p.T3),
                VerticalAlignment = VerticalAlignment.Center,
                UseSystemFocusVisuals = false,
                Content = "改回每日一言",
            };
            resetQuoteBtn.Click += (_, _) =>
            {
                _customVoiceLines.Remove(lang);
                RenderDynamicPage("listening");
                RenderInspector("listening");
            };
            yourLeft.Children.Add(resetQuoteBtn);
            yourLeft.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(12, 12, 3, 12),
                Background = ThemeHelper.Brush(tint.Fill),
                Padding = new Thickness(12, 7, 12, 7),
                MaxWidth = 360,
                Child = new TextBlock
                {
                    Text = userLine,
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Colors.White),
                    TextWrapping = TextWrapping.Wrap,
                },
            });
            yourRow.Children.Add(yourLeft);

            var meAvatar = new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(14),
                Background = ThemeHelper.Brush(p.Inset),
                VerticalAlignment = VerticalAlignment.Bottom,
                Child = new TextBlock { Text = "我", FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T2), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            Grid.SetColumn(meAvatar, 1);
            yourRow.Children.Add(meAvatar);
            topStack.Children.Add(yourRow);
        }
        else
        {
            var curQuote = DailyQuote.Current(_library, lang);
            var quoteStack = new StackPanel { Spacing = 4, Padding = new Thickness(12, 9, 12, 9) };
            var qHead = new Grid();
            var qSourceTb = new TextBlock
            {
                Text = string.IsNullOrEmpty(curQuote.Source) ? L10n.T("每日一言") : $"{L10n.T("每日一言")} · {curQuote.Source}",
                FontSize = 10.5,
                Foreground = ThemeHelper.Brush(p.T3),
                VerticalAlignment = VerticalAlignment.Center,
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            qHead.Children.Add(qSourceTb);

            var qBodyTb = new TextBlock
            {
                Text = curQuote.Text,
                FontFamily = new FontFamily(lang == "en" ? YYFontFamily.LatinSerif.WindowsFamily() : YYFontFamily.Serif.WindowsFamily()),
                FontSize = 12.5,
                LineHeight = 19,
                Foreground = ThemeHelper.Brush(p.T1),
                TextWrapping = TextWrapping.Wrap,
            };

            var refreshQuoteBtn = new Button
            {
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(6, 1, 6, 1),
                CornerRadius = new CornerRadius(6),
                FontSize = 11,
                Foreground = ThemeHelper.Brush(p.T2),
                HorizontalAlignment = HorizontalAlignment.Right,
                UseSystemFocusVisuals = false,
                Content = "↻ 换一句",
            };
            refreshQuoteBtn.Click += async (_, _) =>
            {
                refreshQuoteBtn.IsEnabled = false;
                try
                {
                    var nextQ = await DailyQuote.RefreshAsync(_library, lang);
                    qBodyTb.Text = nextQ.Text;
                    qSourceTb.Text = string.IsNullOrEmpty(nextQ.Source) ? L10n.T("每日一言") : $"{L10n.T("每日一言")} · {nextQ.Source}";
                    if (_activePage == "listening") RenderInspector("listening");
                }
                finally
                {
                    refreshQuoteBtn.IsEnabled = true;
                }
            };
            qHead.Children.Add(refreshQuoteBtn);

            quoteStack.Children.Add(qHead);
            quoteStack.Children.Add(qBodyTb);
            topStack.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(11),
                Background = ThemeHelper.Brush(p.Card),
                BorderBrush = ThemeHelper.Brush(p.Line),
                BorderThickness = new Thickness(0.5),
                Child = quoteStack,
            });

            _ = EnsureVoiceDailyQuoteAsync(lang, qBodyTb, qSourceTb);
        }

        pageGrid.Children.Add(topStack);

        // 6. Middle Row: Scrollable WeChat/iMessage-style VoiceBubble List
        var chatStack = new StackPanel { Spacing = 8, Padding = new Thickness(2, 4, 4, 8) };

        if (srcKey is "kokoro" or "cloned")
        {
            var clonePillSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            clonePillSp.Children.Add(YYIcons.Make("mic", 12.5, ThemeHelper.Brush(tint.Accent)));
            clonePillSp.Children.Add(new TextBlock
            {
                Text = "+ 克隆一个声音 · 念 8 秒，声音不出本机",
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.Medium,
                Foreground = ThemeHelper.Brush(p.T1),
                VerticalAlignment = VerticalAlignment.Center,
            });
            var clonePillBtn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Height = 36,
                CornerRadius = new CornerRadius(18),
                Background = ThemeHelper.Brush(p.Card),
                BorderBrush = ThemeHelper.Brush(p.Line),
                BorderThickness = new Thickness(0.75),
                UseSystemFocusVisuals = false,
                Content = clonePillSp,
            };
            clonePillBtn.Click += async (_, _) =>
            {
                var saved = await VoiceCloneAndDramaDialogs.ShowCloneVoiceDialogAsync(this, RootGrid.XamlRoot, _library);
                if (saved is not null && !_closed)
                {
                    _lastVoiceStructureSig = ComputeVoiceStructureSig();
                    _selectedInspectorVoiceId = saved.Id;
                    RenderDynamicPage("listening");
                    RenderInspector("listening");
                }
            };
            chatStack.Children.Add(clonePillBtn);
        }

        var sourceVoices = BuildVoicesForSource(srcKey, lang);
        if (sourceVoices.Count == 0 && CloudVoiceEntry.TryParseProviderKey(srcKey, out var uncfgProv) && !_library.CloudVoices.IsConfigured(uncfgProv))
        {
            var emptyCfg = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 32, 0, 24) };
            emptyCfg.Children.Add(new TextBlock
            {
                Text = $"尚未连接 {SourceTitleOfKey(srcKey)}",
                FontSize = 13.5,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = ThemeHelper.Brush(p.T1),
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            emptyCfg.Children.Add(new TextBlock
            {
                Text = "请在右侧填写 API Key 或服务地址并验证连接，连接后即可试听全部音色。",
                FontSize = 11.5,
                Foreground = ThemeHelper.Brush(p.T3),
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 300,
            });
            chatStack.Children.Add(emptyCfg);
        }
        else
        {
            foreach (var secGroup in sourceVoices.GroupBy(v => v.SectionTitle))
            {
                if (!string.IsNullOrEmpty(secGroup.Key))
                {
                    var dayLine = new Grid { ColumnSpacing = 10, Margin = new Thickness(8, 4, 8, 2) };
                    dayLine.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    dayLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    dayLine.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    dayLine.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), VerticalAlignment = VerticalAlignment.Center });
                    var secTb = new TextBlock { Text = L10n.T(secGroup.Key), FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3) };
                    Grid.SetColumn(secTb, 1);
                    dayLine.Children.Add(secTb);
                    var rightRule = new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), VerticalAlignment = VerticalAlignment.Center };
                    Grid.SetColumn(rightRule, 2);
                    dayLine.Children.Add(rightRule);
                    chatStack.Children.Add(dayLine);
                }

                foreach (var voice in secGroup)
                {
                    var vItem = voice;
                    bool isChosen = string.Equals(pinnedVoice.Id, vItem.Id, StringComparison.OrdinalIgnoreCase);
                    bool isPlaying = string.Equals(_auditioningVoiceId, vItem.Id, StringComparison.OrdinalIgnoreCase);
                    bool isHeard = isChosen || _heardVoiceIds.Contains(vItem.Id);
                    bool isFocused = string.Equals(_selectedInspectorVoiceId, vItem.Id, StringComparison.OrdinalIgnoreCase);

                    var msgGrid = new Grid { ColumnSpacing = 10, Padding = new Thickness(4, 3, 4, 3) };
                    msgGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
                    msgGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    msgGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var avEl = MakeVoiceAvatar(vItem.Id, vItem.Name, 34);
                    if (avEl is FrameworkElement avFe) avFe.VerticalAlignment = VerticalAlignment.Top;
                    msgGrid.Children.Add(avEl);

                    var bubbleCol = new StackPanel { Spacing = 4 };
                    var nameMeta = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                    nameMeta.Children.Add(new TextBlock
                    {
                        Text = vItem.Name,
                        FontSize = 12,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        Foreground = ThemeHelper.Brush(p.T1),
                    });
                    nameMeta.Children.Add(new TextBlock
                    {
                        Text = vItem.Subtitle,
                        FontSize = 10.5,
                        Foreground = ThemeHelper.Brush(p.T3),
                        MaxLines = 1,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        VerticalAlignment = VerticalAlignment.Bottom,
                    });
                    bubbleCol.Children.Add(nameMeta);

                    var bubbleLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
                    var speechInner = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
                    speechInner.Children.Add(new Border
                    {
                        Width = 22,
                        Height = 22,
                        CornerRadius = new CornerRadius(11),
                        Background = ThemeHelper.Brush(isPlaying ? tint.Fill : p.Inset),
                        Child = YYIcons.Make(isPlaying ? "pause" : "play", 9.5, isPlaying ? new SolidColorBrush(Colors.White) : ThemeHelper.Brush(p.T1)),
                    });

                    var barsPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2.5, VerticalAlignment = VerticalAlignment.Center };
                    Color barRest = IsDark ? Color.FromArgb(115, 224, 220, 212) : Color.FromArgb(255, 206, 202, 194);
                    Color barPlay = tint.Accent;
                    foreach (double bh in DeterministicWaveHeights(vItem.Id, 18, 4.0, 16.0))
                    {
                        barsPanel.Children.Add(new Border
                        {
                            Width = 2.5,
                            Height = bh,
                            CornerRadius = new CornerRadius(1.25),
                            Background = ThemeHelper.Brush(isPlaying ? barPlay : barRest),
                            VerticalAlignment = VerticalAlignment.Center,
                        });
                    }
                    speechInner.Children.Add(barsPanel);
                    speechInner.Children.Add(new TextBlock
                    {
                        Text = $"{estSec}″",
                        FontSize = 11,
                        Foreground = ThemeHelper.Brush(isPlaying ? tint.Accent : p.T3),
                        VerticalAlignment = VerticalAlignment.Center,
                    });

                    var speechBubbleBtn = new Button
                    {
                        Height = 36,
                        Padding = new Thickness(10, 0, 12, 0),
                        CornerRadius = new CornerRadius(4, 14, 14, 14),
                        Background = ThemeHelper.Brush(isPlaying ? tint.Soft : p.Card),
                        BorderBrush = ThemeHelper.Brush(isPlaying || isFocused ? tint.Edge : p.Line),
                        BorderThickness = new Thickness(isPlaying || isFocused ? 1 : 0.75),
                        UseSystemFocusVisuals = false,
                        Content = speechInner,
                    };
                    speechBubbleBtn.Click += async (_, _) => await StartVoiceAuditionAsync(vItem, activeSampleLine);
                    bubbleLine.Children.Add(speechBubbleBtn);

                    if (!isHeard)
                    {
                        bubbleLine.Children.Add(new Ellipse
                        {
                            Width = 6,
                            Height = 6,
                            Fill = ThemeHelper.Brush(p.Danger),
                            VerticalAlignment = VerticalAlignment.Center,
                        });
                    }

                    bubbleCol.Children.Add(bubbleLine);
                    Grid.SetColumn(bubbleCol, 1);
                    msgGrid.Children.Add(bubbleCol);

                    var rightActs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 14, 0, 0) };
                    if (vItem.Id.StartsWith("cloned.", StringComparison.OrdinalIgnoreCase))
                    {
                        var moreClonedBtn = new Button
                        {
                            Style = (Style)Application.Current.Resources["YYIconButtonStyle"],
                            Width = 26,
                            Height = 26,
                            Padding = new Thickness(0),
                            Content = "⋯",
                            FontSize = 12,
                            Foreground = ThemeHelper.Brush(p.T2),
                        };
                        string cid = vItem.Id;
                        moreClonedBtn.Click += async (_, _) =>
                        {
                            await VoiceCloneAndDramaDialogs.ShowClonedCardDetailDialogAsync(this, RootGrid.XamlRoot, _library, cid, () =>
                            {
                                RenderDynamicPage("listening");
                                RenderInspector("listening");
                            });
                        };
                        rightActs.Children.Add(moreClonedBtn);
                    }

                    bool isQueuedPending = !isChosen && IsKokoroVoiceQueuedForLang(vItem, lang);
                    if (isChosen)
                    {
                        rightActs.Children.Add(new Border
                        {
                            Height = 26,
                            CornerRadius = new CornerRadius(13),
                            Padding = new Thickness(10, 0, 10, 0),
                            Background = ThemeHelper.Brush(Color.FromArgb(26, p.Ok.R, p.Ok.G, p.Ok.B)),
                            Child = new TextBlock
                            {
                                Text = "✓ 已选择",
                                FontSize = 11,
                                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                                Foreground = ThemeHelper.Brush(p.Ok),
                                VerticalAlignment = VerticalAlignment.Center,
                            },
                        });
                    }
                    else if (isQueuedPending)
                    {
                        rightActs.Children.Add(new Border
                        {
                            Height = 26,
                            CornerRadius = new CornerRadius(13),
                            Padding = new Thickness(10, 0, 10, 0),
                            Background = ThemeHelper.Brush(tint.Soft),
                            BorderBrush = ThemeHelper.Brush(tint.Edge),
                            BorderThickness = new Thickness(0.5),
                            Child = new TextBlock
                            {
                                Text = L10n.T("下好即启用"),
                                FontSize = 11,
                                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                                Foreground = ThemeHelper.Brush(tint.Accent),
                                VerticalAlignment = VerticalAlignment.Center,
                            },
                        });
                    }
                    else
                    {
                        var chooseBtn = new Button
                        {
                            Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
                            Content = "选择",
                            Height = 26,
                            MinHeight = 26,
                            Padding = new Thickness(11, 0, 11, 0),
                            FontSize = 11,
                        };
                        chooseBtn.Click += (_, _) => SelectVoiceForBookLang(vItem, lang);
                        rightActs.Children.Add(chooseBtn);
                    }

                    Grid.SetColumn(rightActs, 2);
                    msgGrid.Children.Add(rightActs);
                    chatStack.Children.Add(msgGrid);
                }
            }
        }

        var chatScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Disabled,
            Content = chatStack,
        };
        chatScroll.ViewChanged += (_, _) =>
        {
            _voiceChatScrollOffset = chatScroll.VerticalOffset;
        };
        if (_voiceChatScrollOffset > 0)
        {
            double savedOffset = _voiceChatScrollOffset;
            chatScroll.Loaded += (_, _) =>
            {
                if (_closed) return;
                chatScroll.UpdateLayout();
                chatScroll.ChangeView(null, savedOffset, null, disableAnimation: true);
            };
        }
        Grid.SetRow(chatScroll, 1);
        pageGrid.Children.Add(chatScroll);

        // 7. Bottom VoiceComposer: “ QuickLines flyout + MaxLength=120 TextBox + ↑ Audition Button
        var composerGrid = new Grid
        {
            Height = 40,
            Padding = new Thickness(6, 0, 6, 0),
            CornerRadius = new CornerRadius(20),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0.75),
            ColumnSpacing = 6,
        };
        composerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        composerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        composerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        composerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        string draftInit = _draftVoiceLines.TryGetValue(lang, out var dl) ? dl : "";
        var composerBox = new TextBox
        {
            Text = draftInit,
            MaxLength = 120,
            PlaceholderText = lang == "en" ? "Type a sentence to hear them read it…" : "写一句想听他们念的话…",
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(2, 6, 2, 6),
        };
        YYChrome.StripTextBoxChrome(composerBox);
        AutomationProperties.SetName(composerBox, L10n.T("试听句子"));
        var counterTb = new TextBlock
        {
            Text = string.IsNullOrEmpty(draftInit) ? "" : $"{draftInit.Length}/120",
            FontSize = 10.5,
            Foreground = ThemeHelper.Brush(p.T3),
            VerticalAlignment = VerticalAlignment.Center,
        };

        async Task TriggerComposerSendAsync()
        {
            string txt = composerBox.Text.Trim();
            if (string.IsNullOrEmpty(txt))
            {
                txt = activeSampleLine;
            }
            else
            {
                _customVoiceLines[lang] = txt;
                _draftVoiceLines[lang] = "";
            }
            var targetVoice = !string.IsNullOrEmpty(_selectedInspectorVoiceId)
                ? ResolveVoiceItemById(_selectedInspectorVoiceId, lang)
                : pinnedVoice;
            await StartVoiceAuditionAsync(targetVoice, txt, forceRestart: true);
        }

        var quickMenu = new MenuFlyout();
        (string Title, Func<string> GetLine)[] quickItems = lang == "en"
            ?
            [
                ("每日一言", () => { _customVoiceLines.Remove("en"); return DailyQuote.Current(_library, "en").Text; }),
                ("对话", () => "\"Wait a moment,\" she whispered, turning back to the sunlit window. \"Did you hear the bell?\""),
                ("数字", () => "In 1924, Chapter 18 sold 3,450 copies in just 72 hours at $2.50 apiece."),
                ("中英夹杂", () => "The afternoon light fell across《红楼梦》on the desk, quiet and warm."),
                ("书里的一句", () => DailyQuote.PickBookOrFallbackLine(_library, "en")),
            ]
            :
            [
                ("每日一言", () => { _customVoiceLines.Remove("zh"); return DailyQuote.Current(_library, "zh").Text; }),
                ("对话", () => "「你也来了？」她合上书，抬头笑道，「外头风大，快坐下喝杯热茶吧。」"),
                ("数字", () => "1921 年 9 月 25 日，第 3 卷共印了 2,400 册，定价 0.85 元。"),
                ("中英夹杂", () => "他在笔记里写下：Reading in silence 是忙碌一天后最好的休息。"),
                ("书里的一句", () => DailyQuote.PickBookOrFallbackLine(_library, "zh")),
            ];

        foreach (var (qTitle, qFn) in quickItems)
        {
            var mi = new MenuFlyoutItem { Text = L10n.T(qTitle) };
            string capTitle = qTitle;
            var capFn = qFn;
            mi.Click += async (_, _) =>
            {
                string picked = capFn();
                if (capTitle != "每日一言")
                    _customVoiceLines[lang] = picked;
                var target = !string.IsNullOrEmpty(_selectedInspectorVoiceId)
                    ? ResolveVoiceItemById(_selectedInspectorVoiceId, lang)
                    : pinnedVoice;
                await StartVoiceAuditionAsync(target, picked, forceRestart: true);
            };
            quickMenu.Items.Add(mi);
        }

        var quickBtn = new Button
        {
            Width = 28,
            Height = 28,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(14),
            Background = ThemeHelper.Brush(p.Inset),
            BorderThickness = new Thickness(0),
            UseSystemFocusVisuals = false,
            Content = new TextBlock { Text = "“", FontFamily = new FontFamily(YYFontFamily.Serif.WindowsFamily()), FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = ThemeHelper.Brush(p.T2), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) },
            Flyout = quickMenu,
        };
        ToolTipService.SetToolTip(quickBtn, L10n.T("换试听句子"));
        AutomationProperties.SetName(quickBtn, L10n.T("换试听句子"));
        composerGrid.Children.Add(quickBtn);

        composerBox.TextChanged += (_, _) =>
        {
            _draftVoiceLines[lang] = composerBox.Text;
            counterTb.Text = composerBox.Text.Length > 0 ? $"{composerBox.Text.Length}/120" : "";
        };
        composerBox.KeyDown += async (_, ke) =>
        {
            if (ke.Key == VirtualKey.Enter)
            {
                ke.Handled = true;
                await TriggerComposerSendAsync();
            }
        };
        Grid.SetColumn(composerBox, 1);
        composerGrid.Children.Add(composerBox);

        Grid.SetColumn(counterTb, 2);
        composerGrid.Children.Add(counterTb);

        var sendBtn = new Button
        {
            Width = 28,
            Height = 28,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(14),
            Background = ThemeHelper.Brush(p.T1),
            BorderThickness = new Thickness(0),
            UseSystemFocusVisuals = false,
            Content = new TextBlock { Text = "↑", FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = ThemeHelper.Brush(p.Card), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -1, 0, 0) },
        };
        ToolTipService.SetToolTip(sendBtn, L10n.T("试听这一句"));
        AutomationProperties.SetName(sendBtn, L10n.T("试听这一句"));
        sendBtn.Click += async (_, _) => await TriggerComposerSendAsync();
        Grid.SetColumn(sendBtn, 3);
        composerGrid.Children.Add(sendBtn);

        Grid.SetRow(composerGrid, 2);
        pageGrid.Children.Add(composerGrid);

        VoiceSourceHost.Children.Add(pageGrid);
    }

    private void BuildListeningInspector(YYPalette p, YYTintColors tint)
    {
        bool inSourcePage = !string.IsNullOrEmpty(_focusedVoiceSource);
        bool needsCloudConnect = inSourcePage
            && CloudVoiceEntry.TryParseProviderKey(_focusedVoiceSource, out var cpCheck)
            && cpCheck is not CloudTtsProvider.Edge and not CloudTtsProvider.Free
            && !_library.CloudVoices.IsConfigured(cpCheck);

        // When viewing overview OR inside VoiceSourcePage with ⚙ config mode off, render ListeningVoiceCard + 30-bar VoicePlayer!
        if (!inSourcePage || (!_voiceSourceConfigOpen && !needsCloudConnect))
        {
            string lang = inSourcePage ? (_voiceBookLang == "en" ? "en" : "zh") : "zh";
            string pinnedId = GetActiveVoiceIdForLang(lang);
            var pinnedItem = ResolveVoiceItemById(pinnedId, lang);

            VoiceCatalogItem targetVoice;
            if (!string.IsNullOrEmpty(_auditioningVoiceId))
                targetVoice = ResolveVoiceItemById(_auditioningVoiceId, lang);
            else if (inSourcePage && !string.IsNullOrEmpty(_selectedInspectorVoiceId))
                targetVoice = ResolveVoiceItemById(_selectedInspectorVoiceId, lang);
            else
                targetVoice = pinnedItem;

            bool isSelectedForLang = string.Equals(targetVoice.Id, pinnedItem.Id, StringComparison.OrdinalIgnoreCase);

            // Recent voice change undo pill (valid for 10 minutes)
            if (_recentVoiceSwap is { } swap && swap.Lang == lang && (DateTimeOffset.Now - swap.At).TotalMinutes < 10)
            {
                var swapGrid = new Grid { ColumnSpacing = 6, Padding = new Thickness(10, 7, 10, 7) };
                swapGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                swapGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                swapGrid.Children.Add(new TextBlock
                {
                    Text = $"{swap.NewName} 换下了 {swap.OldName}",
                    FontSize = 11,
                    Foreground = ThemeHelper.Brush(p.T2),
                    VerticalAlignment = VerticalAlignment.Center,
                    MaxLines = 1,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
                var revertBtn = new Button
                {
                    Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
                    Content = "↶ 换回",
                    Height = 22,
                    MinHeight = 22,
                    Padding = new Thickness(7, 0, 7, 0),
                    FontSize = 10.5,
                };
                revertBtn.Click += (_, _) =>
                {
                    _recentVoiceSwap = null;
                    var oldItem = ResolveVoiceItemById(swap.OldId, lang);
                    SelectVoiceForBookLang(oldItem, lang);
                };
                Grid.SetColumn(revertBtn, 1);
                swapGrid.Children.Add(revertBtn);
                InspectorHost.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(9),
                    Background = ThemeHelper.Brush(tint.Soft),
                    BorderBrush = ThemeHelper.Brush(tint.Edge),
                    BorderThickness = new Thickness(0.5),
                    Child = swapGrid,
                });
            }

            string topHeading = isSelectedForLang
                ? (lang == "en" ? "在念你的英文书" : "在念你的中文书")
                : "正在试听";
            InspectorHost.Children.Add(new TextBlock
            {
                Text = L10n.T(topHeading),
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = ThemeHelper.Brush(p.T2),
            });

            var headRow = new Grid { ColumnSpacing = 10 };
            headRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            headRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            headRow.Children.Add(MakeVoiceAvatar(targetVoice.Id, targetVoice.Name, 36));

            var headInfo = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            headInfo.Children.Add(new TextBlock { Text = targetVoice.Name, FontSize = 13.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
            headInfo.Children.Add(new TextBlock { Text = L10n.T(targetVoice.SourceName), FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(headInfo, 1);
            headRow.Children.Add(headInfo);

            if (isSelectedForLang)
            {
                var selPill = new Border
                {
                    CornerRadius = new CornerRadius(12),
                    Height = 24,
                    Padding = new Thickness(9, 0, 9, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Background = ThemeHelper.Brush(Color.FromArgb(26, p.Ok.R, p.Ok.G, p.Ok.B)),
                    Child = new TextBlock { Text = L10n.T("✓ 已选择"), FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.Ok), VerticalAlignment = VerticalAlignment.Center },
                };
                Grid.SetColumn(selPill, 2);
                headRow.Children.Add(selPill);
            }
            else if (IsKokoroVoiceQueuedForLang(targetVoice, lang))
            {
                var queuedPill = new Border
                {
                    CornerRadius = new CornerRadius(12),
                    Height = 24,
                    Padding = new Thickness(9, 0, 9, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Background = ThemeHelper.Brush(tint.Soft),
                    BorderBrush = ThemeHelper.Brush(tint.Edge),
                    BorderThickness = new Thickness(0.5),
                    Child = new TextBlock { Text = L10n.T("下好即启用"), FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(tint.Accent), VerticalAlignment = VerticalAlignment.Center },
                };
                Grid.SetColumn(queuedPill, 2);
                headRow.Children.Add(queuedPill);
            }
            else
            {
                var chooseBtn = new Button
                {
                    Style = (Style)Application.Current.Resources["YYPrimaryPillButtonStyle"],
                    Content = L10n.T("选择"),
                    Height = 25,
                    MinHeight = 25,
                    Padding = new Thickness(10, 0, 10, 0),
                    FontSize = 11,
                };
                chooseBtn.Click += (_, _) => SelectVoiceForBookLang(targetVoice, lang);
                Grid.SetColumn(chooseBtn, 2);
                headRow.Children.Add(chooseBtn);
            }
            InspectorHost.Children.Add(headRow);

            // 30-bar waveform progress VoicePlayer + progressive text preview (1:1 MacVoicePicker.swift:1951-2113)
            string sampleText = !string.IsNullOrEmpty(_auditioningVoiceId) && !string.IsNullOrEmpty(_auditioningLineText)
                ? _auditioningLineText
                : GetActiveVoiceSampleLine(lang);
            int totalSecs = EstimateSpeakSeconds(sampleText, _library.Prefs.SpeechRate);
            bool isPlayingThis = string.Equals(_auditioningVoiceId, targetVoice.Id, StringComparison.OrdinalIgnoreCase);

            var playRow = new Grid { ColumnSpacing = 9 };
            playRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
            playRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            playRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var playCircle = new Button
            {
                Width = 32,
                Height = 32,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(16),
                Background = ThemeHelper.Brush(p.T1),
                BorderThickness = new Thickness(0),
                UseSystemFocusVisuals = false,
                Content = YYIcons.Make(isPlayingThis ? "pause" : "play", 11.5, ThemeHelper.Brush(p.Card)),
            };
            AutomationProperties.SetName(playCircle, L10n.T(isPlayingThis ? "暂停试听" : "播放试听"));
            playCircle.Click += async (_, _) => await StartVoiceAuditionAsync(targetVoice, sampleText);
            playRow.Children.Add(playCircle);

            const int barCount = 30;
            var waveGrid = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            var barBorders = new Border[barCount];
            Color barUnplayed = IsDark ? Color.FromArgb(96, 232, 224, 212) : Color.FromArgb(255, 220, 217, 212);
            Color barPlayed = p.Danger;
            double[] heights30 = DeterministicWaveHeights(targetVoice.Id + "|player", barCount, 4.0, 18.0);
            for (int bi = 0; bi < barCount; bi++)
            {
                var bb = new Border
                {
                    Width = 2.2,
                    Height = heights30[bi],
                    CornerRadius = new CornerRadius(1.1),
                    Background = ThemeHelper.Brush(barUnplayed),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                barBorders[bi] = bb;
                waveGrid.Children.Add(bb);
            }
            Grid.SetColumn(waveGrid, 1);
            playRow.Children.Add(waveGrid);

            var durTb = new TextBlock
            {
                Text = $"{totalSecs}″",
                FontSize = 11,
                Foreground = ThemeHelper.Brush(p.T3),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(durTb, 2);
            playRow.Children.Add(durTb);

            var spokenRun = new Microsoft.UI.Xaml.Documents.Run { Text = isPlayingThis ? "" : sampleText, Foreground = ThemeHelper.Brush(p.T1) };
            var remainRun = new Microsoft.UI.Xaml.Documents.Run { Text = isPlayingThis ? sampleText : "", Foreground = ThemeHelper.Brush(p.T3) };
            var previewTb = new TextBlock
            {
                FontSize = 12.5,
                LineHeight = 20,
                TextWrapping = TextWrapping.Wrap,
            };
            previewTb.Inlines.Add(spokenRun);
            previewTb.Inlines.Add(remainRun);

            if (isPlayingThis)
            {
                void SyncWaveProgress()
                {
                    double elapsed = (DateTimeOffset.Now - _auditionStartAt).TotalSeconds;
                    double frac = Math.Clamp(elapsed / Math.Max(0.8, _auditionEstimatedSecs), 0.0, 0.98);
                    int litBars = (int)Math.Round(frac * barCount);
                    for (int bi = 0; bi < barCount; bi++)
                        barBorders[bi].Background = ThemeHelper.Brush(bi < litBars ? barPlayed : barUnplayed);
                    int splitIdx = Math.Clamp((int)Math.Round(frac * sampleText.Length), 0, sampleText.Length);
                    spokenRun.Text = sampleText[..splitIdx];
                    remainRun.Text = sampleText[splitIdx..];
                }
                SyncWaveProgress();
                _voicePlayTimer?.Stop();
                _voicePlayTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
                _voicePlayTimer.Tick += (_, _) =>
                {
                    if (!string.Equals(_auditioningVoiceId, targetVoice.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        _voicePlayTimer?.Stop();
                        return;
                    }
                    SyncWaveProgress();
                };
                _voicePlayTimer.Start();
            }

            var audStack = new StackPanel { Spacing = 12 };
            audStack.Children.Add(playRow);
            audStack.Children.Add(previewTb);

            InspectorHost.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(12),
                Background = ThemeHelper.Brush(p.Card),
                BorderBrush = ThemeHelper.Brush(p.Line),
                BorderThickness = new Thickness(0.5),
                Padding = new Thickness(14, 12, 14, 12),
                Child = audStack,
            });

            InspectorHost.Children.Add(MakeCard(
                MakeRow("来自", null, new TextBlock { Text = L10n.T(targetVoice.SourceName), FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T2) }, compact: true),
                MakeRow("费用", null, new TextBlock { Text = L10n.T(SourceCostLabel(targetVoice.SourceKey)), FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T2) }, compact: true)));
            return;
        }

        // Source Configuration Panel (when ⚙ _voiceSourceConfigOpen is toggled on, or cloud source is unconfigured)
        if (_voiceSourceConfigOpen)
        {
            var backToPlayerBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
                Content = "‹ 返回试听面板",
                Height = 25,
                MinHeight = 25,
                Padding = new Thickness(9, 0, 10, 0),
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            backToPlayerBtn.Click += (_, _) =>
            {
                _voiceSourceConfigOpen = false;
                RenderDynamicPage("listening");
                RenderInspector("listening");
            };
            InspectorHost.Children.Add(backToPlayerBtn);
        }

        if (_focusedVoiceSource is "kokoro" or "cloned")
        {
            var ko = _library.Kokoro;
            var lv = _library.LiveVoice;

            var kHead = new Grid { ColumnSpacing = 10 };
            kHead.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            kHead.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            kHead.Children.Add(YYChrome.MakeServiceTile("kokoro", IsDark, 32));
            var kMeta = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            kMeta.Children.Add(new TextBlock { Text = "语阅离线语音 (Kokoro)", FontSize = 13.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
            kMeta.Children.Add(new TextBlock { Text = "本机离线神经网络引擎 · Apache-2.0", FontSize = 11, Foreground = ThemeHelper.Brush(p.T3) });
            Grid.SetColumn(kMeta, 1);
            kHead.Children.Add(kMeta);
            InspectorHost.Children.Add(kHead);

            int koInitPct = (int)Math.Round(ko.DownloadProgress * 100);
            bool koFailed = ko.State == KokoroStateKind.Failed && !string.IsNullOrWhiteSpace(ko.StatusMessage);
            var koSubTb = new TextBlock
            {
                Text = ko.IsInstalled
                    ? L10n.T("已就绪 · 16 个中英音色")
                    : (ko.State == KokoroStateKind.Downloading
                        ? L10n.T($"正在下载轻量离线语音 {koInitPct}%…")
                        : (koFailed ? L10n.T(ko.StatusMessage) : L10n.T("断网秒出声，低内存占用"))),
                FontSize = 10.5,
                Foreground = ThemeHelper.Brush(koFailed ? p.Danger : (ko.State == KokoroStateKind.Downloading ? tint.Accent : p.T3)),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 16,
            };
            var koBar = new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Value = koInitPct,
                Height = 4,
                Visibility = ko.State == KokoroStateKind.Downloading ? Visibility.Visible : Visibility.Collapsed,
                Margin = new Thickness(0, 2, 0, 0),
            };
            var koActionBtn = new Button
            {
                Style = (Style)Application.Current.Resources[ko.IsInstalled || ko.State == KokoroStateKind.Downloading ? "YYPillButtonStyle" : "YYPrimaryPillButtonStyle"],
                Content = L10n.T(ko.IsInstalled ? "移除" : (ko.State == KokoroStateKind.Downloading ? $"取消 ({koInitPct}%)" : (ko.State == KokoroStateKind.Failed ? "重试下载" : $"下载 ({KokoroVoiceModel.TotalMb} MB)"))),
                Height = 25,
                MinHeight = 25,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
            };
            AutomationProperties.SetName(koActionBtn, L10n.T("Kokoro 轻量旁白"));
            if (ko.IsInstalled) koActionBtn.Foreground = ThemeHelper.Brush(p.Danger);
            koActionBtn.Click += async (_, _) =>
            {
                if (ko.IsInstalled)
                {
                    bool ok = await YYChrome.ShowConfirmAsync(
                        RootGrid.XamlRoot,
                        IsDark,
                        L10n.T("移除 Kokoro 离线语音包？"),
                        L10n.T("移除后将释放约 125 MB 空间，随时可重新免费下载。"),
                        confirmText: L10n.T("移除"),
                        cancelText: L10n.T("取消"),
                        destructive: true,
                        tint: _library.Prefs.Tint);
                    if (!ok || _closed) return;
                    koActionBtn.IsEnabled = false;
                    await Task.Run(() => ko.DeleteInstalledModel());
                    if (_closed) return;
                    App.CurrentApp.BroadcastVoiceChanged();
                }
                else if (ko.State == KokoroStateKind.Downloading)
                {
                    ko.CancelDownload();
                }
                else
                {
                    _ = ko.DownloadAsync();
                }
            };

            var koRow = new Grid { ColumnSpacing = 12, Padding = new Thickness(16, 8, 16, 8), MinHeight = 42 };
            koRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            koRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var koLeft = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            koLeft.Children.Add(new TextBlock { Text = L10n.T("Kokoro 轻量旁白"), FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1) });
            koLeft.Children.Add(koSubTb);
            koLeft.Children.Add(koBar);
            koRow.Children.Add(koLeft);
            Grid.SetColumn(koActionBtn, 1);
            koRow.Children.Add(koActionBtn);

            int lvInitPct = (int)Math.Round(lv.DownloadProgress * 100);
            bool lvFailed = lv.State == LiveVoiceStateKind.Failed && !string.IsNullOrWhiteSpace(lv.StatusMessage);
            var lvSubTb = new TextBlock
            {
                Text = lv.IsReady
                    ? L10n.T("已就绪 · 支持声音克隆与多角色")
                    : (lv.State == LiveVoiceStateKind.Downloading
                        ? L10n.T($"正在下载克隆与角色引擎 {lvInitPct}%…")
                        : (lvFailed ? L10n.T(lv.StatusMessage) : L10n.T("本地 24kHz 模型，供声音克隆与广播剧使用"))),
                FontSize = 10.5,
                Foreground = ThemeHelper.Brush(lvFailed ? p.Danger : (lv.State == LiveVoiceStateKind.Downloading ? tint.Accent : p.T3)),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 16,
            };
            var lvBar = new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Value = lvInitPct,
                Height = 4,
                Visibility = lv.State == LiveVoiceStateKind.Downloading ? Visibility.Visible : Visibility.Collapsed,
                Margin = new Thickness(0, 2, 0, 0),
            };
            var lvActionBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
                Content = L10n.T(lv.IsReady ? "移除" : (lv.State == LiveVoiceStateKind.Downloading ? $"取消 ({lvInitPct}%)" : (lv.State == LiveVoiceStateKind.Failed ? "重试下载" : "下载 (160 MB)"))),
                Height = 25,
                MinHeight = 25,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
            };
            AutomationProperties.SetName(lvActionBtn, L10n.T("广播剧与克隆引擎"));
            if (lv.IsReady) lvActionBtn.Foreground = ThemeHelper.Brush(p.Danger);
            lvActionBtn.Click += async (_, _) =>
            {
                if (lv.IsReady)
                {
                    bool ok = await YYChrome.ShowConfirmAsync(
                        RootGrid.XamlRoot,
                        IsDark,
                        L10n.T("移除精做离线角色模型？"),
                        L10n.T("你的克隆声音卡会保留在本机，模型随时可重新下载。"),
                        confirmText: L10n.T("移除"),
                        cancelText: L10n.T("取消"),
                        destructive: true,
                        tint: _library.Prefs.Tint);
                    if (!ok || _closed) return;
                    lvActionBtn.IsEnabled = false;
                    await Task.Run(() => lv.DeleteInstalledModels());
                    if (_closed) return;
                    App.CurrentApp.BroadcastVoiceChanged();
                }
                else if (lv.State == LiveVoiceStateKind.Downloading)
                {
                    lv.CancelDownload();
                }
                else
                {
                    _ = lv.DownloadAsync();
                }
            };

            var lvRow = new Grid { ColumnSpacing = 12, Padding = new Thickness(16, 8, 16, 8), MinHeight = 42 };
            lvRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            lvRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var lvLeft = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            lvLeft.Children.Add(new TextBlock { Text = L10n.T("广播剧与克隆引擎"), FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1) });
            lvLeft.Children.Add(lvSubTb);
            lvLeft.Children.Add(lvBar);
            lvRow.Children.Add(lvLeft);
            Grid.SetColumn(lvActionBtn, 1);
            lvRow.Children.Add(lvActionBtn);

            _inspectorVoiceProgressUpdater = () =>
            {
                if (ko.State == KokoroStateKind.Downloading)
                {
                    int pct = (int)Math.Round(ko.DownloadProgress * 100);
                    koSubTb.Text = L10n.T($"正在下载轻量离线语音 {pct}%…");
                    koBar.Visibility = Visibility.Visible;
                    koBar.Value = pct;
                    koActionBtn.Content = L10n.T($"取消 ({pct}%)");
                }
                if (lv.State == LiveVoiceStateKind.Downloading)
                {
                    int pct = (int)Math.Round(lv.DownloadProgress * 100);
                    lvSubTb.Text = L10n.T($"正在下载克隆与角色引擎 {pct}%…");
                    lvBar.Visibility = Visibility.Visible;
                    lvBar.Value = pct;
                    lvActionBtn.Content = L10n.T($"取消 ({pct}%)");
                }
            };

            var preRenderSw = MakeSwitch(_library.Prefs.DramaPreRenderPhone, v =>
            {
                bool prev = !v;
                _library.Prefs.DramaPreRenderPhone = v;
                _library.SavePrefs();
                RecordPrefChange(
                    nameof(DevicePrefs.DramaPreRenderPhone),
                    "给我的手机提前做好",
                    v ? "开" : "关",
                    prev ? "开" : "关",
                    prev,
                    v,
                    undo: () => { _library.Prefs.DramaPreRenderPhone = prev; _library.SavePrefs(); },
                    redo: () => { _library.Prefs.DramaPreRenderPhone = v; _library.SavePrefs(); });
            });

            InspectorHost.Children.Add(MakeCard(
                koRow,
                lvRow,
                MakeRow("给我的手机提前做好", "接电源时为附近手机自动预合成后续章节", preRenderSw, compact: true)));
            return;
        }

        if (_focusedVoiceSource == "pc")
        {
            InspectorHost.Children.Add(new TextBlock
            {
                Text = "这台电脑 · 系统语音",
                FontSize = 13.5,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = ThemeHelper.Brush(p.T1),
            });
            InspectorHost.Children.Add(MakeCard(
                MakeRow("合成引擎", "Windows SAPI / OneCore 系统内置语音", null, compact: true),
                MakeRow("费用", null, new TextBlock { Text = "免费 · 不联网", FontSize = 11.5, Foreground = ThemeHelper.Brush(p.Ok) }, compact: true)));
            InspectorHost.Children.Add(new TextBlock
            {
                Text = "系统自带语音停顿较生硬，仅建议在离线且尚未下载 Kokoro 时作临时兜底。",
                FontSize = 11,
                Foreground = ThemeHelper.Brush(p.T3),
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        // Cloud & Local TTS Provider Config Panel
        if (!CloudVoiceEntry.TryParseProviderKey(_focusedVoiceSource, out var cloudProv))
            cloudProv = CloudTtsProvider.OpenAi;

        string pKey = CloudVoiceEntry.ProviderKey(cloudProv);
        string title = CloudVoiceService.ProviderDisplayName(cloudProv, _library.Prefs);
        bool isLocal = CloudVoiceService.IsLocalProvider(cloudProv);

        InspectorHost.Children.Add(new TextBlock { Text = title, FontSize = 13.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
        InspectorHost.Children.Add(new TextBlock { Text = CloudVoiceService.ProviderHelp(cloudProv), FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), TextWrapping = TextWrapping.Wrap });

        var stack = new StackPanel { Spacing = 10 };
        PasswordBox? kb = null;

        if (cloudProv == CloudTtsProvider.Edge)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "免注册免填 Key，直连微软 Edge 22 个中英日多语种神经音色（晓晓、云希、Ava 等）；已合成的句子自动缓存在本机。",
                FontSize = 11.5,
                Foreground = ThemeHelper.Brush(p.T2),
                TextWrapping = TextWrapping.Wrap,
            });
        }
        else if (cloudProv == CloudTtsProvider.Free)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "零配置免 Key 即用，直连开源 Kokoro 在线推理服务；已合成的句子自动缓存在本机。",
                FontSize = 11.5,
                Foreground = ThemeHelper.Brush(p.T2),
                TextWrapping = TextWrapping.Wrap,
            });
        }
        else
        {
            if (isLocal)
            {
                if (cloudProv == CloudTtsProvider.Local)
                {
                    var customNameBox = new TextBox
                    {
                        Header = "显示名称",
                        Text = _library.Prefs.CloudVoiceCustomNames.TryGetValue("local", out var cn) ? cn : "",
                        PlaceholderText = "例如：家里的 Kokoro",
                        FontSize = 12,
                    };
                    customNameBox.TextChanged += (_, _) =>
                    {
                        _library.Prefs.CloudVoiceCustomNames["local"] = customNameBox.Text.Trim();
                        _library.SavePrefs();
                        RenderDynamicPage("listening");
                    };
                    stack.Children.Add(customNameBox);
                }

                var urlBox = new TextBox
                {
                    Header = "服务地址 (OpenAI 兼容 /v1)",
                    Text = _library.CloudVoices.GetSavedUrl(cloudProv),
                    PlaceholderText = CloudVoiceService.DefaultLocalUrl(cloudProv),
                    FontSize = 12,
                };
                urlBox.TextChanged += (_, _) =>
                {
                    _library.Prefs.CloudVoiceBaseUrls[pKey] = urlBox.Text.Trim();
                    _library.SavePrefs();
                };
                stack.Children.Add(urlBox);

                var mdlBox = new TextBox
                {
                    Header = "模型名",
                    Text = _library.CloudVoices.GetSavedModel(cloudProv),
                    PlaceholderText = CloudVoiceService.DefaultLocalModel(cloudProv),
                    FontSize = 12,
                };
                mdlBox.TextChanged += (_, _) =>
                {
                    _library.Prefs.CloudVoiceModels[pKey] = mdlBox.Text.Trim();
                    _library.SavePrefs();
                };
                stack.Children.Add(mdlBox);
            }

            if (cloudProv == CloudTtsProvider.Volcengine)
            {
                var appBox = new TextBox
                {
                    Header = "火山引擎 AppID",
                    Text = _library.Secrets.Get("tts.volcengine.appid"),
                    PlaceholderText = "填写应用 AppID…",
                    FontSize = 12,
                };
                appBox.TextChanged += (_, _) =>
                {
                    _library.Secrets.Set("tts.volcengine.appid", appBox.Text.Trim());
                };
                stack.Children.Add(appBox);
            }

            kb = new PasswordBox
            {
                Header = isLocal
                    ? "API Key（局域网可选，本机可留空）"
                    : (cloudProv == CloudTtsProvider.Volcengine ? "Access Token（本机加密存储）" : "API Key（本机加密存储）"),
                Password = _library.CloudVoices.GetApiKey(cloudProv),
                PlaceholderText = CloudVoiceService.ProviderKeyHint(cloudProv),
            };
            kb.PasswordChanged += (_, _) =>
            {
                _library.CloudVoices.SetApiKey(cloudProv, kb.Password.Trim());
            };
            stack.Children.Add(kb);
        }

        if (!isLocal && kb is not null && string.IsNullOrEmpty(kb.Password) && _library.CloudVoices.GetCompanionSharedKey(cloudProv) is { Length: > 0 } sharedKey)
        {
            var reuseBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Content = "复用「书伴」的 Key",
                FontSize = 11.5,
            };
            reuseBtn.Click += (_, _) =>
            {
                _library.CloudVoices.SetApiKey(cloudProv, sharedKey);
                kb.Password = sharedKey;
                RenderDynamicPage("listening");
                RenderInspector("listening");
            };
            stack.Children.Add(reuseBtn);
        }

        if (cloudProv == CloudTtsProvider.Azure)
        {
            var regBox = new TextBox
            {
                Header = "服务区域 (Region)",
                Text = _library.Prefs.CloudVoiceAzureRegion,
                PlaceholderText = "例如：eastasia / southeastasia / eastus / japaneast",
                FontSize = 12,
            };
            regBox.TextChanged += (_, _) =>
            {
                _library.Prefs.CloudVoiceAzureRegion = regBox.Text.Trim();
                _library.SavePrefs();
            };
            stack.Children.Add(regBox);
        }

        var checkStatus = new TextBlock { FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), TextWrapping = TextWrapping.Wrap };
        var refreshBtn = new Button
        {
            Style = (Style)Application.Current.Resources["YYPrimaryPillButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = cloudProv is CloudTtsProvider.Edge or CloudTtsProvider.Free
                ? "测试连通并刷新音色"
                : (isLocal ? "探测连接并启用" : "验证连接并刷新音色列表"),
        };
        refreshBtn.Click += async (_, _) =>
        {
            refreshBtn.IsEnabled = false;
            checkStatus.Text = "正在连接并拉取音色…";
            var (ok, _, msg) = await _library.CloudVoices.RefreshVoicesAsync(cloudProv);
            checkStatus.Text = msg;
            checkStatus.Foreground = ThemeHelper.Brush(ok ? p.Ok : p.Danger);
            refreshBtn.IsEnabled = true;
            if (ok)
            {
                _voiceSourceConfigOpen = false;
                App.CurrentApp.BroadcastVoiceChanged();
                RenderDynamicPage("listening");
                RenderInspector("listening");
            }
        };
        stack.Children.Add(refreshBtn);
        stack.Children.Add(checkStatus);

        InspectorHost.Children.Add(new Border { CornerRadius = new CornerRadius(11), Background = ThemeHelper.Brush(p.Card), BorderBrush = ThemeHelper.Brush(p.Line), BorderThickness = new Thickness(1), Padding = new Thickness(12), Child = stack });

        if (cloudProv is CloudTtsProvider.Edge or CloudTtsProvider.Free && _library.CloudVoices.IsConfigured(cloudProv))
        {
            string dKey = cloudProv == CloudTtsProvider.Edge ? "tts.edge.dismissed" : "tts.free.dismissed";
            var hideBtn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
                Foreground = ThemeHelper.Brush(p.Danger),
                Content = $"隐藏 {title}",
            };
            hideBtn.Click += (_, _) =>
            {
                _library.Secrets.Set(dKey, "1");
                SetFocusedVoiceSource("");
                _voiceSourceConfigOpen = false;
                App.CurrentApp.BroadcastVoiceChanged();
                RenderDynamicPage("listening");
                RenderInspector("listening");
            };
            InspectorHost.Children.Add(hideBtn);
        }
        else if (_library.CloudVoices.IsConfigured(cloudProv))
        {
            var discBtn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
                Foreground = ThemeHelper.Brush(p.Danger),
                Content = $"断开 {title}",
            };
            discBtn.Click += async (_, _) =>
            {
                bool ok = await YYChrome.ShowConfirmAsync(
                    RootGrid.XamlRoot,
                    IsDark,
                    L10n.T($"断开 {title}？"),
                    L10n.T("会清除本机保存的该语音服务凭证，需要时可重新添加。"),
                    confirmText: L10n.T("断开"),
                    cancelText: L10n.T("取消"),
                    destructive: true,
                    tint: _library.Prefs.Tint);
                if (!ok) return;

                _library.CloudVoices.SetApiKey(cloudProv, "");
                if (cloudProv == CloudTtsProvider.Volcengine) _library.Secrets.Set("tts.volcengine.appid", "");
                _library.Prefs.CloudVoiceEnabled.Remove(pKey);
                if (isLocal) _library.Prefs.CloudVoiceDismissed.Add(pKey);
                LocalVoiceProbe.Shared.Clear(cloudProv);
                _library.SavePrefs();
                SetFocusedVoiceSource("");
                _voiceSourceConfigOpen = false;
                App.CurrentApp.BroadcastVoiceChanged();
                RenderDynamicPage("listening");
                RenderInspector("listening");
            };
            InspectorHost.Children.Add(discBtn);
        }
    }

    // ---------- 5. 书伴（1:1 MacCompanionSettings.swift & MacCompanionConnect.swift） ----------

    private static string FormatFriendlyModelName(string rawId)
    {
        if (string.IsNullOrWhiteSpace(rawId)) return "";
        string id = rawId.Trim();
        string slug = id.Contains('/') ? id[(id.LastIndexOf('/') + 1)..] : id;
        int colonIdx = slug.IndexOf(":free", StringComparison.OrdinalIgnoreCase);
        if (colonIdx > 0) slug = slug[..colonIdx];

        return slug.ToLowerInvariant() switch
        {
            "auto" when id.StartsWith("openrouter/", StringComparison.OrdinalIgnoreCase) => "OpenRouter Auto",
            "claude-opus-4-6" or "claude-opus-4.6" => "Claude Opus 4.6",
            "claude-sonnet-4-6" or "claude-sonnet-4.6" => "Claude Sonnet 4.6",
            "claude-sonnet-4-5" or "claude-sonnet-4.5" => "Claude Sonnet 4.5",
            "claude-haiku-4-5" or "claude-haiku-4.5" => "Claude Haiku 4.5",
            "claude-opus-4-5" or "claude-opus-4.5" => "Claude Opus 4.5",
            "gpt-5" => "GPT-5",
            "gpt-5-mini" => "GPT-5 mini",
            "gpt-4.1" => "GPT-4.1",
            "gpt-4.1-mini" => "GPT-4.1 mini",
            "gpt-4.1-nano" => "GPT-4.1 nano",
            "gpt-4o" => "GPT-4o",
            "gpt-4o-mini" => "GPT-4o mini",
            "o3" => "OpenAI o3",
            "o4-mini" => "OpenAI o4-mini",
            "deepseek-chat" or "deepseek-v3" => "DeepSeek V3",
            "deepseek-reasoner" or "deepseek-r1" => "DeepSeek R1",
            "gemini-2.5-pro" => "Gemini 2.5 Pro",
            "gemini-2.5-flash" => "Gemini 2.5 Flash",
            "gemini-2.0-flash" => "Gemini 2.0 Flash",
            "qwen-max" => "Qwen Max",
            "qwen-plus" => "Qwen Plus",
            "qwen-turbo" => "Qwen Turbo",
            "kimi-k2-turbo-preview" or "kimi-k2" => "Kimi K2 Turbo",
            "moonshot-v1-32k" => "Moonshot v1 32K",
            "glm-4.6" => "GLM-4.6",
            "glm-4-flash" => "GLM-4 Flash",
            "doubao-seed-1-6-250615" => "Doubao Seed 1.6",
            _ => slug,
        };
    }

    private static string MaskSecretKey(string rawKey)
    {
        if (string.IsNullOrEmpty(rawKey)) return "";
        if (rawKey.Length > 8) return $"{rawKey[..3]}••••{rawKey[^4..]}";
        return "••••";
    }

    private string ShortProviderTitle(string id, string fallback) =>
        _library.Prefs.AiCustomProviderNames.TryGetValue(id, out var cname) && !string.IsNullOrWhiteSpace(cname)
            ? cname
            : id switch
            {
                "openrouter" => "OpenRouter",
                "claude" => "Claude (Anthropic)",
                "openai" => "OpenAI",
                "gemini" => "Gemini (Google)",
                "deepseek" => "DeepSeek",
                "dashscope" => "通义千问",
                "moonshot" => "Kimi",
                "zhipu" => "智谱 GLM",
                "volcengine" => "豆包",
                "siliconflow" => "硅基流动",
                "minimax" => "MiniMax",
                "xai" => "Grok",
                "mistral" => "Mistral",
                "groq" => "Groq",
                "magpie" => "magpie 网关",
                "ollama" => "Ollama（本机）",
                "lmstudio" => "LM Studio（本机）",
                "custom" => "自定义兼容服务",
                _ => fallback,
            };

    private AiProviderPreset ResolveCompanionPreset(string pid)
    {
        if (CompanionClient.FindPreset(pid) is { } found) return found;
        string cname = _library.Prefs.AiCustomProviderNames.TryGetValue(pid, out var n) && !string.IsNullOrWhiteSpace(n) ? n : "自定义服务";
        return new AiProviderPreset(pid, cname, "", "", false, Group: "key", KeyHint: "可不填");
    }

    private Border MakeServiceLogoTile(string id, double size = 30, string? customMark = null) =>
        YYChrome.MakeServiceTile(id == "free" ? "online" : id, IsDark, size);

    private string GetProviderModel(string pid, string defaultModel) =>
        _library.Prefs.AiProviderModels.TryGetValue(pid, out var m) && !string.IsNullOrWhiteSpace(m)
            ? m
            : (pid == _library.Prefs.AiProvider && !string.IsNullOrWhiteSpace(_library.Prefs.AiModel) ? _library.Prefs.AiModel : defaultModel);

    private string GetProviderUrl(string pid, string defaultUrl) =>
        _library.Prefs.AiProviderUrls.TryGetValue(pid, out var u) && !string.IsNullOrWhiteSpace(u)
            ? u
            : (pid == _library.Prefs.AiProvider && !string.IsNullOrWhiteSpace(_library.Prefs.AiBaseUrl) ? _library.Prefs.AiBaseUrl : defaultUrl);

    private void ActivateCompanionProvider(string pid)
    {
        var preset = ResolveCompanionPreset(pid);
        _library.Prefs.AiProvider = pid;
        _library.Prefs.AiModel = GetProviderModel(pid, preset.DefaultModel);
        _library.Prefs.AiBaseUrl = GetProviderUrl(pid, preset.BaseUrl);
        string pKey = _library.Secrets.Get($"ai.key.{pid}");
        _library.Secrets.Set("ai.apiKey", pKey);
        _library.SavePrefs();
    }

    private void ScheduleCompanionPageRefresh()
    {
        if (_closed) return;
        if (_companionDebounceTimer is null)
        {
            _companionDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            _companionDebounceTimer.Tick += (_, _) =>
            {
                _companionDebounceTimer.Stop();
                if (!_closed && _activePage == "companion")
                    RenderDynamicPage("companion");
            };
        }
        _companionDebounceTimer.Stop();
        _companionDebounceTimer.Start();
    }

    private void FlushCompanionPageRefresh()
    {
        if (_companionDebounceTimer is { IsEnabled: true })
        {
            _companionDebounceTimer.Stop();
            if (!_closed && _activePage == "companion")
                RenderDynamicPage("companion");
        }
    }

    private void BuildCompanionPage()
    {
        var p = CurrentPalette;
        var tint = CurrentTint;
        DynamicPageHost.Children.Add(MakeHeading("模型服务"));

        if (!_library.Prefs.ConfiguredAiProviders.Contains(_library.Prefs.AiProvider))
            _library.Prefs.ConfiguredAiProviders.Insert(0, _library.Prefs.AiProvider);

        var listStack = new StackPanel { Spacing = 2, Padding = new Thickness(4) };
        var configured = _library.Prefs.ConfiguredAiProviders
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(ResolveCompanionPreset)
            .ToArray();

        for (int i = 0; i < configured.Length; i++)
        {
            if (i > 0) listStack.Children.Add(new Border { Height = 1, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(12, 0, 12, 0) });
            var preset = configured[i];
            bool selected = _focusedCompanionId == preset.Id;
            bool inUse = _library.Prefs.AiProvider == preset.Id;

            string rawKey = _library.Secrets.Get($"ai.key.{preset.Id}");
            string masked = MaskSecretKey(rawKey);
            string model = GetProviderModel(preset.Id, preset.DefaultModel);
            string friendlyModel = FormatFriendlyModelName(model);

            var rowGrid = new Grid { ColumnSpacing = 12, MinHeight = 46, Padding = new Thickness(12, 8, 12, 8) };
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rowGrid.Children.Add(MakeServiceLogoTile(preset.Id, 30));

            var mid = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            var titleLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
            titleLine.Children.Add(new TextBlock { Text = ShortProviderTitle(preset.Id, preset.Name), FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1) });
            if (inUse) titleLine.Children.Add(MakeStatusPill("当前使用", p.Ok));
            mid.Children.Add(titleLine);

            string detail = preset.Group == "local"
                ? $"{friendlyModel} · 本机离线运行"
                : $"{friendlyModel}{(string.IsNullOrEmpty(masked) ? " · 未填 Key" : $" · Key {masked}")}";
            mid.Children.Add(new TextBlock { Text = detail, FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(mid, 1);
            rowGrid.Children.Add(mid);

            var chev = YYIcons.Make("chevron-right", 10.5, ThemeHelper.Brush(p.T3));
            Grid.SetColumn(chev, 2);
            rowGrid.Children.Add(chev);

            var btn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(9),
                BorderThickness = new Thickness(0),
                Background = selected ? ThemeHelper.Brush(Color.FromArgb(16, p.T1.R, p.T1.G, p.T1.B)) : new SolidColorBrush(Colors.Transparent),
                UseSystemFocusVisuals = false,
                Content = rowGrid,
            };
            string pid = preset.Id;
            btn.Click += (_, _) =>
            {
                _focusedCompanionId = pid;
                _companionPickingModel = false;
                _companionEditingKey = false;
                RenderDynamicPage("companion");
                RenderInspector("companion");
            };
            listStack.Children.Add(btn);
        }

        // + 添加服务商行
        listStack.Children.Add(new Border { Height = 1, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(12, 0, 12, 0) });
        var addGrid = new Grid { ColumnSpacing = 12, MinHeight = 46, Padding = new Thickness(12, 8, 12, 8) };
        addGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        addGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        addGrid.Children.Add(new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(8), Background = ThemeHelper.Brush(tint.Soft), Child = YYIcons.Make("plus", 12.5, ThemeHelper.Brush(tint.Accent)) });
        var addMeta = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        addMeta.Children.Add(new TextBlock { Text = "添加服务商…", FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(tint.Accent) });
        addMeta.Children.Add(new TextBlock { Text = "Claude、OpenAI、DeepSeek、Gemini、Kimi、硅基流动、本机 Ollama…", FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(addMeta, 1);
        addGrid.Children.Add(addMeta);

        var addBtn = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(9),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            UseSystemFocusVisuals = false,
            Content = addGrid,
        };
        addBtn.Click += async (_, _) => await ShowAddProviderSheetAsync();
        listStack.Children.Add(addBtn);

        DynamicPageHost.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(1),
            Child = listStack,
        });

        DynamicPageHost.Children.Add(MakeNote("API Key 仅保存在这台电脑上（Windows DPAPI 加密），不同步、不上传；调用费用由服务商直接结算。播客文稿生成同样使用此处所选模型。"));

        if (!_localCompanionProbeStarted)
        {
            _localCompanionProbeStarted = true;
            _ = Task.Run(async () =>
            {
                var found = await CompanionClient.ProbeLocalProvidersAsync();
                if (found.Count > 0)
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (_closed) return;
                        _liveLocalCompanions = found;
                        if (_activePage == "companion") RenderDynamicPage("companion");
                    });
                }
            });
        }

        if (_liveLocalCompanions.Count > 0)
        {
            var localFoundRows = new List<UIElement>();
            foreach (var kv in _liveLocalCompanions)
            {
                string lpid = kv.Key;
                var lpreset = ResolveCompanionPreset(lpid);
                bool added = _library.Prefs.ConfiguredAiProviders.Contains(lpid);
                var useLocalBtn = new Button
                {
                    Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
                    Content = added ? "已添加" : "一键添加",
                    IsEnabled = !added,
                    Height = 25,
                    MinHeight = 25,
                    FontSize = 11,
                };
                useLocalBtn.Click += (_, _) =>
                {
                    bool wasNew = !_library.Prefs.ConfiguredAiProviders.Contains(lpid, StringComparer.OrdinalIgnoreCase);
                    if (wasNew)
                    {
                        _library.Prefs.ConfiguredAiProviders.Add(lpid);
                        _newlyAddedCompanionIds.Add(lpid);
                    }
                    if (kv.Value.Count > 0) _library.Prefs.AiProviderModels[lpid] = kv.Value[0];
                    _focusedCompanionId = lpid;
                    _companionPickingModel = false;
                    _companionEditingKey = false;
                    if (string.IsNullOrWhiteSpace(_library.Prefs.AiProvider) || !_library.Companion.IsConfigured)
                    {
                        _newlyAddedCompanionIds.Remove(lpid);
                        ActivateCompanionProvider(lpid);
                    }
                    else
                    {
                        _library.SavePrefs();
                    }
                    RenderDynamicPage("companion");
                    RenderInspector("companion");
                };
                string modelsSummary = kv.Value.Count > 0 ? $"检测到 {kv.Value.Count} 个本机模型（如 {kv.Value[0]}）" : "本机服务正在运行";
                localFoundRows.Add(MakeRow($"● {lpreset.Name} 在线", modelsSummary, useLocalBtn, compact: true));
            }
            DynamicPageHost.Children.Add(MakeHeading("本机已运行的服务", "完全离线，不花 API 费用"));
            DynamicPageHost.Children.Add(MakeCard(localFoundRows.ToArray()));
        }

        // 用量（当 TotalTokens == 0 时隐藏空柱状图与 0% 占位标签；当云端朗读为 0 时隐藏云端朗读行）
        var snap = _library.Usage.Snapshot30Days();
        var todayRec = _library.Usage.GetToday();
        DynamicPageHost.Children.Add(MakeHeading("用量"));

        var usageTop = new StackPanel { Spacing = 8, Padding = new Thickness(16, 14, 16, 14) };
        var usageHead = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        usageHead.Children.Add(new TextBlock { Text = "Token 用量", FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
        usageHead.Children.Add(new TextBlock { Text = "近 30 天", FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Bottom });
        usageTop.Children.Add(usageHead);
        usageTop.Children.Add(new TextBlock
        {
            Text = CompanionUsageLedger.FormatCount(snap.TotalTokens),
            FontSize = 28,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = ThemeHelper.Brush(p.T1),
        });
        string hitStr = snap.CacheHitRate is double hr ? $" · 缓存命中 {Math.Round(hr * 100)}%" : "";
        usageTop.Children.Add(new TextBlock
        {
            Text = snap.TotalTokens == 0 ? "近 30 天暂无调用记录" : $"今日 {CompanionUsageLedger.FormatCount(todayRec.TotalTokens)}{hitStr}",
            FontSize = 11.5,
            Foreground = ThemeHelper.Brush(p.T3),
        });

        if (snap.TotalTokens > 0 && snap.Days.Count > 0)
        {
            const double chartHeight = 84.0;
            long peak = Math.Max(1, snap.PeakTokens);
            var dayDetailText = new TextBlock { FontSize = 11, Foreground = ThemeHelper.Brush(p.T2) };
            var dayDetailBox = new Border
            {
                CornerRadius = new CornerRadius(7),
                Background = ThemeHelper.Brush(p.Inset),
                Padding = new Thickness(10, 5, 10, 5),
                Visibility = Visibility.Collapsed,
                Child = dayDetailText,
            };

            var chartRow = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 4, 0, 0) };
            chartRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            chartRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            if (snap.PeakTokens > 0)
            {
                var yAxis = new Grid { Height = chartHeight, MinWidth = 34 };
                yAxis.Children.Add(new TextBlock { Text = CompanionUsageLedger.FormatCount(snap.PeakTokens), FontSize = 9.5, Foreground = ThemeHelper.Brush(p.T3), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top });
                long avgRounded = (long)Math.Round(snap.AverageTokens);
                if (avgRounded > 0 && avgRounded < snap.PeakTokens * 0.85)
                {
                    double avgFrac = 1.0 - Math.Clamp(snap.AverageTokens / peak, 0.1, 0.9);
                    yAxis.Children.Add(new TextBlock { Text = CompanionUsageLedger.FormatCount(avgRounded), FontSize = 9.5, Foreground = ThemeHelper.Brush(p.T3), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, Math.Round(chartHeight * avgFrac) - 6, 0, 0) });
                }
                yAxis.Children.Add(new TextBlock { Text = "0", FontSize = 9.5, Foreground = ThemeHelper.Brush(p.T3), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom });
                chartRow.Children.Add(yAxis);
            }

            var plotArea = new Grid { Height = chartHeight };
            plotArea.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), VerticalAlignment = VerticalAlignment.Top });
            plotArea.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), VerticalAlignment = VerticalAlignment.Bottom });
            if (snap.PeakTokens > 0 && snap.AverageTokens > 0)
            {
                double avgTop = Math.Round(chartHeight * (1.0 - Math.Clamp(snap.AverageTokens / peak, 0.05, 0.95)));
                plotArea.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), Opacity = 0.65, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, avgTop, 0, 0) });
            }

            var barsGrid = new Grid { ColumnSpacing = 2.5, Height = chartHeight };
            var outBrush = ThemeHelper.Brush(tint.Accent);
            var missBrush = ThemeHelper.Brush(Color.FromArgb((byte)(IsDark ? 150 : 120), tint.Accent.R, tint.Accent.G, tint.Accent.B));
            var cacheBrush = ThemeHelper.Brush(Color.FromArgb((byte)(IsDark ? 175 : 145), p.Ok.R, p.Ok.G, p.Ok.B));
            var zeroTickBrush = ThemeHelper.Brush(p.Inset);

            for (int di = 0; di < snap.Days.Count; di++)
            {
                barsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var day = snap.Days[di];
                long dayTotal = day.TotalTokens;

                var colHost = new Grid { Background = new SolidColorBrush(Colors.Transparent), VerticalAlignment = VerticalAlignment.Stretch };
                if (dayTotal > 0 && snap.PeakTokens > 0)
                {
                    double totalH = Math.Max(3.0, chartHeight * ((double)dayTotal / peak));
                    double outH = totalH * ((double)day.Output / dayTotal);
                    double missH = totalH * ((double)day.MissInput / dayTotal);
                    double cacheH = Math.Max(0, totalH - outH - missH);

                    var barStack = new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Bottom, CornerRadius = new CornerRadius(2, 2, 0, 0) };
                    if (outH >= 0.5) barStack.Children.Add(new Border { Height = outH, Background = outBrush });
                    if (missH >= 0.5) barStack.Children.Add(new Border { Height = missH, Background = missBrush });
                    if (cacheH >= 0.5) barStack.Children.Add(new Border { Height = cacheH, Background = cacheBrush });
                    if (barStack.Children.Count == 0) barStack.Children.Add(new Border { Height = 3, Background = outBrush });

                    colHost.Children.Add(barStack);
                    string tip = $"{day.Date} · 总 {dayTotal:N0}" +
                                 (day.CachedInput > 0 ? $" · 缓存 {day.CachedInput:N0}" : "") +
                                 $" · 输出 {day.Output:N0} · {day.Requests} 次";
                    ToolTipService.SetToolTip(colHost, tip);
                    colHost.PointerEntered += (_, _) => { dayDetailText.Text = tip; dayDetailBox.Visibility = Visibility.Visible; };
                    colHost.PointerPressed += (_, _) =>
                    {
                        dayDetailText.Text = tip;
                        dayDetailBox.Visibility = dayDetailBox.Visibility == Visibility.Visible && dayDetailText.Text == tip
                            ? Visibility.Collapsed
                            : Visibility.Visible;
                    };
                }
                else
                {
                    colHost.Children.Add(new Border { Height = 2, CornerRadius = new CornerRadius(1), Background = zeroTickBrush, VerticalAlignment = VerticalAlignment.Bottom });
                }

                Grid.SetColumn(colHost, di);
                barsGrid.Children.Add(colHost);
            }

            barsGrid.PointerExited += (_, _) => dayDetailBox.Visibility = Visibility.Collapsed;
            plotArea.Children.Add(barsGrid);
            Grid.SetColumn(plotArea, 1);
            chartRow.Children.Add(plotArea);
            usageTop.Children.Add(chartRow);

            var xAxisRow = new Grid { ColumnSpacing = 8 };
            xAxisRow.ColumnDefinitions.Add(new ColumnDefinition { Width = snap.PeakTokens > 0 ? new GridLength(34) : new GridLength(0) });
            xAxisRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var xLabels = new Grid();
            xLabels.Children.Add(new TextBlock { Text = snap.Days[0].ShortLabel, FontSize = 9.5, Foreground = ThemeHelper.Brush(p.T3), HorizontalAlignment = HorizontalAlignment.Left });
            if (snap.PeakTokens > 0 && snap.PeakIndex > 3 && snap.PeakIndex < snap.Days.Count - 4)
                xLabels.Children.Add(new TextBlock { Text = $"峰值 {snap.Days[snap.PeakIndex].ShortLabel}", FontSize = 9.5, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(tint.Accent), HorizontalAlignment = HorizontalAlignment.Center });
            xLabels.Children.Add(new TextBlock { Text = snap.Days[^1].ShortLabel, FontSize = 9.5, Foreground = ThemeHelper.Brush(p.T3), HorizontalAlignment = HorizontalAlignment.Right });
            Grid.SetColumn(xLabels, 1);
            xAxisRow.Children.Add(xLabels);
            usageTop.Children.Add(xAxisRow);
            usageTop.Children.Add(dayDetailBox);

            var legend = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
            void AddLegendItem(Brush swatch, string label)
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
                sp.Children.Add(new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(2), Background = swatch, VerticalAlignment = VerticalAlignment.Center });
                sp.Children.Add(new TextBlock { Text = label, FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Center });
                legend.Children.Add(sp);
            }
            if (snap.CachedInputTokens > 0) AddLegendItem(cacheBrush, "缓存命中输入");
            if (snap.MissInputTokens > 0) AddLegendItem(missBrush, "常规输入");
            if (snap.OutputTokens > 0) AddLegendItem(outBrush, "输出");
            usageTop.Children.Add(legend);

            if (snap.Sources.Count > 0)
            {
                var chipsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                foreach (var share in snap.Sources)
                {
                    int pct = Math.Max(1, (int)Math.Round(share.Share * 100));
                    string srcTitle = CompanionUsageLedger.SourceTitle(share.Source);
                    var chipSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
                    chipSp.Children.Add(new TextBlock { Text = srcTitle, FontSize = 11, Foreground = ThemeHelper.Brush(p.T2) });
                    chipSp.Children.Add(new TextBlock { Text = $"{pct}%", FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
                    chipSp.Children.Add(new TextBlock { Text = CompanionUsageLedger.FormatCount(share.Tokens), FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3) });
                    chipsRow.Children.Add(new Border
                    {
                        CornerRadius = new CornerRadius(11),
                        Background = ThemeHelper.Brush(p.Inset),
                        Padding = new Thickness(9, 3, 9, 3),
                        Child = chipSp,
                    });
                }
                usageTop.Children.Add(new ScrollViewer
                {
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    HorizontalScrollMode = ScrollMode.Auto,
                    VerticalScrollMode = ScrollMode.Disabled,
                    Margin = new Thickness(0, 2, 0, 0),
                    Content = chipsRow,
                });
            }
        }

        if (snap.TtsChars > 0 || snap.TtsSavedChars > 0)
        {
            string ttsText = snap.TtsSavedChars > 0
                ? $"{CompanionUsageLedger.FormatCount(snap.TtsChars)} 字 · 本地缓存省 {CompanionUsageLedger.FormatCount(snap.TtsSavedChars)} 字"
                : $"{CompanionUsageLedger.FormatCount(snap.TtsChars)} 字";
            var ttsUsageRow = MakeRow("云端朗读 · 近 30 天", null,
                new TextBlock { Text = ttsText, FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1) },
                compact: true);
            DynamicPageHost.Children.Add(MakeCard(usageTop, ttsUsageRow));
        }
        else
        {
            DynamicPageHost.Children.Add(MakeCard(usageTop));
        }

        // 省费（1:1 MacCompanionSettings.swift: 仅保留 2 行）
        DynamicPageHost.Children.Add(MakeHeading("省费"));
        var llmCapSeg = YYChrome.MakeSegmentedBar(
            [("0", "不限"), ("200000", "20 万"), ("500000", "50 万"), ("1000000", "100 万")],
            _library.Prefs.AiDailyLlmTokenCap.ToString(),
            s =>
            {
                long v = long.TryParse(s, out var parsed) ? parsed : 0;
                long prev = _library.Prefs.AiDailyLlmTokenCap;
                if (prev == v) return;
                _library.Prefs.AiDailyLlmTokenCap = v;
                _library.SavePrefs();
                RecordPrefChange(
                    nameof(DevicePrefs.AiDailyLlmTokenCap),
                    "每日用量上限",
                    v == 0 ? "不限" : CompanionUsageLedger.FormatCount(v),
                    prev == 0 ? "不限" : CompanionUsageLedger.FormatCount(prev),
                    prev,
                    v,
                    undo: () => { _library.Prefs.AiDailyLlmTokenCap = prev; _library.SavePrefs(); },
                    redo: () => { _library.Prefs.AiDailyLlmTokenCap = v; _library.SavePrefs(); });
                RenderDynamicPage("companion");
            },
            IsDark, fontSize: 11.5, height: 28);

        var tierSw = MakeSwitch(_library.Prefs.AiTierRoutingEnabled, v =>
        {
            bool prev = !v;
            _library.Prefs.AiTierRoutingEnabled = v;
            _library.SavePrefs();
            RecordPrefChange(
                nameof(DevicePrefs.AiTierRoutingEnabled),
                "批量任务自动用轻量模型",
                v ? "开" : "关",
                prev ? "开" : "关",
                prev,
                v,
                undo: () => { _library.Prefs.AiTierRoutingEnabled = prev; _library.SavePrefs(); },
                redo: () => { _library.Prefs.AiTierRoutingEnabled = v; _library.SavePrefs(); });
        });

        DynamicPageHost.Children.Add(MakeCard(
            MakeRow("批量任务自动用轻量模型", "认人、错字整理、提要优先用同厂轻量模型，并自动启用提示词缓存", tierSw),
            MakeRow("每日用量上限", "达到后自动暂停后台批量任务", llmCapSeg)));
        DynamicPageHost.Children.Add(MakeNote("统计为本机估算值，以服务商实际账单为准。书伴问答与前情提要的纯文本缓存会随同步自动共享。"));
    }

    private async Task ShowAddVoiceSourceDialogAsync()
    {
        var p = CurrentPalette;
        var tint = CurrentTint;
        ContentDialog? dlgRef = null;
        var stageHost = new Grid();

        static (string Url, string Trail) ProviderKeyPortal(CloudTtsProvider prov) => prov switch
        {
            CloudTtsProvider.Volcengine => ("https://console.volcengine.com/speech/app", "console.volcengine.com › 语音技术 › 创建应用获取 AppID 与 Token。"),
            CloudTtsProvider.Xiaomi => ("https://platform.xiaomimimo.com", "platform.xiaomimimo.com › API Keys › 新建密钥。"),
            CloudTtsProvider.SiliconFlow => ("https://cloud.siliconflow.cn/account/ak", "cloud.siliconflow.cn › API 密钥 › 新建。"),
            CloudTtsProvider.DashScope => ("https://bailian.console.aliyun.com/", "bailian.console.aliyun.com › API-KEY › 创建密钥。"),
            CloudTtsProvider.MiniMax => ("https://platform.minimaxi.com/user-center/basic-information/interface-key", "platform.minimaxi.com › 接口密钥 › 新建。"),
            CloudTtsProvider.Fish => ("https://fish.audio/zh-CN/go-api/", "fish.audio › API › 新建 API Key。"),
            CloudTtsProvider.Azure => ("https://portal.azure.com/", "portal.azure.com › 语音服务 › 密钥和终结点。"),
            CloudTtsProvider.OpenAi => ("https://platform.openai.com/api-keys", "platform.openai.com › API keys › 新建，拷下来。"),
            CloudTtsProvider.ElevenLabs => ("https://elevenlabs.io/app/settings/api-keys", "elevenlabs.io › Developers › API Keys › 新建。"),
            CloudTtsProvider.Deepgram => ("https://console.deepgram.com/", "console.deepgram.com › API Keys › Create Key。"),
            _ => ("", ""),
        };

        void RenderPickerStep()
        {
            stageHost.Children.Clear();
            var outerStack = new StackPanel { Spacing = 0 };
            var wrap = new StackPanel { Spacing = 14, Padding = new Thickness(22, 20, 22, 14) };
            wrap.Children.Add(YYChrome.MakeSheetHeader("添加声音来源", "朗读", () => dlgRef?.Hide(), IsDark, titleSize: 18));

            var searchRow = new Grid
            {
                Height = 36,
                Padding = new Thickness(11, 0, 11, 0),
                CornerRadius = new CornerRadius(10),
                Background = ThemeHelper.Brush(p.Inset),
                ColumnSpacing = 8,
            };
            searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            searchRow.Children.Add(YYIcons.Make("search", 13.5, ThemeHelper.Brush(p.T3)));
            var sBox = new TextBox
            {
                PlaceholderText = "搜索语音服务，比如 豆包、OpenAI、MiMo、VoiceStudio…",
                BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(Colors.Transparent),
                FontSize = 12.5,
                VerticalAlignment = VerticalAlignment.Center,
                Padding = new Thickness(0, 6, 0, 6),
            };
            YYChrome.StripTextBoxChrome(sBox);
            AutomationProperties.SetName(sBox, L10n.T("搜索语音服务"));
            Grid.SetColumn(sBox, 1);
            searchRow.Children.Add(sBox);
            wrap.Children.Add(searchRow);

            var groupsHost = new StackPanel { Spacing = 14 };
            void PopulateVoiceProviders(string qRaw)
            {
                groupsHost.Children.Clear();
                string q = qRaw.Trim().ToLowerInvariant();
                bool edgeDismissed = _library.Secrets.Get("tts.edge.dismissed") == "1" || !_library.CloudVoices.IsConfigured(CloudTtsProvider.Edge);
                bool freeDismissed = _library.Secrets.Get("tts.free.dismissed") == "1" || !_library.CloudVoices.IsConfigured(CloudTtsProvider.Free);

                // Fix P0-4: include Edge & Free when dismissed so user can re-enable in 1 click; exclude Local (pinned at bottom)
                var all = CloudVoiceService.AllProviders
                    .Where(x => x != CloudTtsProvider.Local)
                    .Where(x => (x != CloudTtsProvider.Edge || edgeDismissed) && (x != CloudTtsProvider.Free || freeDismissed))
                    .Where(x => string.IsNullOrEmpty(q)
                        || CloudVoiceEntry.ProviderKey(x).Contains(q, StringComparison.OrdinalIgnoreCase)
                        || CloudVoiceService.ProviderDisplayName(x, _library.Prefs).Contains(q, StringComparison.OrdinalIgnoreCase)
                        || CloudVoiceService.ProviderHelp(x).Contains(q, StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                // Optional top group for dismissed free online sources
                var freeItems = all.Where(x => x is CloudTtsProvider.Edge or CloudTtsProvider.Free).ToArray();
                if (freeItems.Length > 0)
                {
                    var freeSec = new StackPanel { Spacing = 6 };
                    freeSec.Children.Add(new TextBlock { Text = L10n.T("免 Key 在线语音（一键恢复）"), FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.Ok), Margin = new Thickness(4, 0, 0, 0) });
                    var freeGrid = new Grid { ColumnSpacing = 8, RowSpacing = 6 };
                    for (int c = 0; c < 3; c++) freeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    for (int i = 0; i < freeItems.Length; i++)
                    {
                        var pr = freeItems[i];
                        string pk = CloudVoiceEntry.ProviderKey(pr);
                        string iconKey = pr == CloudTtsProvider.Free ? "online" : pk;
                        var cell = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9, VerticalAlignment = VerticalAlignment.Center };
                        cell.Children.Add(YYChrome.MakeBareServiceIcon(iconKey, IsDark, 20));
                        cell.Children.Add(new TextBlock
                        {
                            Text = CloudVoiceService.ProviderDisplayName(pr, _library.Prefs),
                            FontSize = 12.5,
                            Foreground = ThemeHelper.Brush(p.T1),
                            VerticalAlignment = VerticalAlignment.Center,
                            MaxLines = 1,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                        });
                        var cellBtn = new Button
                        {
                            HorizontalAlignment = HorizontalAlignment.Stretch,
                            HorizontalContentAlignment = HorizontalAlignment.Left,
                            Padding = new Thickness(6, 7, 6, 7),
                            CornerRadius = new CornerRadius(8),
                            Background = new SolidColorBrush(Colors.Transparent),
                            BorderThickness = new Thickness(0),
                            UseSystemFocusVisuals = false,
                            Content = cell,
                        };
                        var capturedFree = pr;
                        cellBtn.Click += (_, _) =>
                        {
                            string dismissKey = capturedFree == CloudTtsProvider.Edge ? "tts.edge.dismissed" : "tts.free.dismissed";
                            _library.Secrets.Set(dismissKey, "");
                            SetFocusedVoiceSource(CloudVoiceEntry.ProviderKey(capturedFree));
                            _voiceSourceConfigOpen = false;
                            App.CurrentApp.BroadcastVoiceChanged();
                            dlgRef?.Hide();
                            RenderDynamicPage("listening");
                            RenderInspector("listening");
                            ShowFlashBanner($"✓ 已重新启用 {CloudVoiceService.ProviderDisplayName(capturedFree, _library.Prefs)}");
                        };
                        Grid.SetColumn(cellBtn, i % 3);
                        freeGrid.Children.Add(cellBtn);
                    }
                    freeSec.Children.Add(freeGrid);
                    groupsHost.Children.Add(freeSec);
                }

                var regular = all.Where(x => x is not CloudTtsProvider.Edge and not CloudTtsProvider.Free).ToArray();
                foreach (var (grp, grpTitle) in new[]
                {
                    (CloudProviderGroup.Domestic, "国内服务"),
                    (CloudProviderGroup.Global, "国际服务"),
                    (CloudProviderGroup.Local, "本机与局域网"),
                })
                {
                    var items = regular.Where(x => CloudVoiceService.ProviderGroup(x) == grp).ToArray();
                    if (items.Length == 0) continue;

                    var sec = new StackPanel { Spacing = 6 };
                    sec.Children.Add(new TextBlock { Text = grpTitle, FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T3), Margin = new Thickness(4, 0, 0, 0) });

                    var grid = new Grid { ColumnSpacing = 8, RowSpacing = 6 };
                    for (int c = 0; c < 3; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    int rCount = (items.Length + 2) / 3;
                    for (int r = 0; r < rCount; r++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                    for (int i = 0; i < items.Length; i++)
                    {
                        var pr = items[i];
                        string pk = CloudVoiceEntry.ProviderKey(pr);
                        bool cfg = _library.CloudVoices.IsConfigured(pr);
                        var cell = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9, VerticalAlignment = VerticalAlignment.Center };
                        cell.Children.Add(YYChrome.MakeBareServiceIcon(pk, IsDark, 20));
                        cell.Children.Add(new TextBlock
                        {
                            Text = cfg ? $"{CloudVoiceService.ProviderDisplayName(pr, _library.Prefs)} ✓" : CloudVoiceService.ProviderDisplayName(pr, _library.Prefs),
                            FontSize = 12.5,
                            Foreground = ThemeHelper.Brush(cfg ? p.T3 : p.T1),
                            VerticalAlignment = VerticalAlignment.Center,
                            MaxLines = 1,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                        });
                        var cellBtn = new Button
                        {
                            HorizontalAlignment = HorizontalAlignment.Stretch,
                            HorizontalContentAlignment = HorizontalAlignment.Left,
                            Padding = new Thickness(6, 7, 6, 7),
                            CornerRadius = new CornerRadius(8),
                            Background = new SolidColorBrush(Colors.Transparent),
                            BorderThickness = new Thickness(0),
                            UseSystemFocusVisuals = false,
                            Content = cell,
                        };
                        var captured = pr;
                        cellBtn.Click += (_, _) => RenderConnectStep(captured);
                        Grid.SetRow(cellBtn, i / 3);
                        Grid.SetColumn(cellBtn, i % 3);
                        grid.Children.Add(cellBtn);
                    }
                    sec.Children.Add(grid);
                    groupsHost.Children.Add(sec);
                }
            }

            sBox.TextChanged += (_, _) => PopulateVoiceProviders(sBox.Text);
            PopulateVoiceProviders("");
            wrap.Children.Add(new ScrollViewer { MaxHeight = 320, Content = groupsHost });
            outerStack.Children.Add(wrap);

            // Pinned bottom row: "+ 自定义语音接口" (1:1 MacListeningSettings.swift)
            outerStack.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line) });
            var customTtsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            customTtsRow.Children.Add(YYIcons.Make("plus", 13, ThemeHelper.Brush(p.T2)));
            customTtsRow.Children.Add(new TextBlock { Text = "自定义语音接口", FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1), VerticalAlignment = VerticalAlignment.Center });
            customTtsRow.Children.Add(new TextBlock { Text = "GPT-SoVITS、Kokoro、unspeech 或局域网 /v1/audio/speech", FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Center, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
            var customTtsBtn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(22, 12, 22, 12),
                CornerRadius = new CornerRadius(0),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                UseSystemFocusVisuals = false,
                Content = customTtsRow,
            };
            customTtsBtn.Click += (_, _) => RenderConnectStep(CloudTtsProvider.Local);
            outerStack.Children.Add(customTtsBtn);

            L10n.ApplyTree(outerStack);
            stageHost.Children.Add(outerStack);
        }

        void RenderConnectStep(CloudTtsProvider prov)
        {
            stageHost.Children.Clear();
            string pk = CloudVoiceEntry.ProviderKey(prov);
            string dispName = CloudVoiceService.ProviderDisplayName(prov, _library.Prefs);
            bool isLocal = CloudVoiceService.IsLocalProvider(prov);
            int vCount = _library.CloudVoices.GetVoices(prov).Count;

            var wrap = new StackPanel { Spacing = 12, Padding = new Thickness(22, 20, 22, 20) };
            wrap.Children.Add(YYChrome.MakeSheetHeader(
                dispName,
                "朗读",
                () => dlgRef?.Hide(),
                IsDark,
                leading: YYChrome.MakeServiceTile(pk, IsDark, 28),
                titleSize: 18));

            string summary = isLocal
                ? CloudVoiceService.ProviderHelp(prov)
                : $"{Math.Max(vCount, 4)} 个音色，支持中英朗读与本机持久缓存";
            wrap.Children.Add(new TextBlock { Text = summary, FontSize = 12.5, Foreground = ThemeHelper.Brush(p.T2), TextWrapping = TextWrapping.Wrap });

            var cardStack = new StackPanel { Spacing = 0 };
            TextBox? customNameField = null;
            TextBox? urlBox = null;
            TextBox? extraBox = null;
            PasswordBox? keyBox = null;

            Grid MakeRecessedFieldRow(string label, FrameworkElement editor)
            {
                var rg = new Grid
                {
                    Margin = new Thickness(5, 5, 5, 0),
                    Padding = new Thickness(12, 6, 10, 6),
                    MinHeight = 40,
                    CornerRadius = new CornerRadius(9),
                    Background = ThemeHelper.Brush(p.Inset),
                    ColumnSpacing = 10,
                };
                rg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                rg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                rg.Children.Add(new TextBlock { Text = label, FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1), VerticalAlignment = VerticalAlignment.Center });
                Grid.SetColumn(editor, 1);
                rg.Children.Add(editor);
                return rg;
            }

            if (isLocal)
            {
                if (prov == CloudTtsProvider.Local)
                {
                    customNameField = new TextBox
                    {
                        Text = _library.Prefs.CloudVoiceCustomNames.TryGetValue("local", out var cn) ? cn : "",
                        PlaceholderText = "例如：家里的 Kokoro",
                        BorderThickness = new Thickness(0),
                        Background = new SolidColorBrush(Colors.Transparent),
                        FontSize = 12.5,
                        TextAlignment = TextAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    YYChrome.StripTextBoxChrome(customNameField);
                    AutomationProperties.SetName(customNameField, L10n.T("名称"));
                    cardStack.Children.Add(MakeRecessedFieldRow("名称", customNameField));
                }

                urlBox = new TextBox
                {
                    Text = _library.CloudVoices.GetSavedUrl(prov),
                    PlaceholderText = CloudVoiceService.DefaultLocalUrl(prov),
                    BorderThickness = new Thickness(0),
                    Background = new SolidColorBrush(Colors.Transparent),
                    FontSize = 12.5,
                    TextAlignment = TextAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                YYChrome.StripTextBoxChrome(urlBox);
                AutomationProperties.SetName(urlBox, L10n.T("地址"));
                cardStack.Children.Add(MakeRecessedFieldRow("地址", urlBox));
            }
            else
            {
                if (prov == CloudTtsProvider.Volcengine)
                {
                    extraBox = new TextBox
                    {
                        Text = _library.Secrets.Get("tts.volcengine.appid"),
                        PlaceholderText = "AppID",
                        BorderThickness = new Thickness(0),
                        Background = new SolidColorBrush(Colors.Transparent),
                        FontSize = 12.5,
                        TextAlignment = TextAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    YYChrome.StripTextBoxChrome(extraBox);
                    AutomationProperties.SetName(extraBox, "AppID");
                    cardStack.Children.Add(MakeRecessedFieldRow("AppID", extraBox));
                }
                else if (prov == CloudTtsProvider.Azure)
                {
                    extraBox = new TextBox
                    {
                        Text = _library.Prefs.CloudVoiceAzureRegion,
                        PlaceholderText = "eastasia",
                        BorderThickness = new Thickness(0),
                        Background = new SolidColorBrush(Colors.Transparent),
                        FontSize = 12.5,
                        TextAlignment = TextAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    YYChrome.StripTextBoxChrome(extraBox);
                    AutomationProperties.SetName(extraBox, L10n.T("区域"));
                    cardStack.Children.Add(MakeRecessedFieldRow("区域", extraBox));
                }

                string existingKey = _library.CloudVoices.GetApiKey(prov);
                keyBox = new PasswordBox
                {
                    Password = existingKey,
                    PlaceholderText = CloudVoiceService.ProviderKeyHint(prov),
                    BorderThickness = new Thickness(0),
                    Background = new SolidColorBrush(Colors.Transparent),
                    FontSize = 12.5,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                YYChrome.StripPasswordBoxChrome(keyBox);
                AutomationProperties.SetName(keyBox, "API Key");
                cardStack.Children.Add(MakeRecessedFieldRow("Key", keyBox));
            }

            var statusRight = new TextBlock
            {
                Text = isLocal ? "探测本机端口" : "先填 Key",
                FontSize = 11.5,
                Foreground = ThemeHelper.Brush(p.T3),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            var verifyLeft = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, VerticalAlignment = VerticalAlignment.Center };
            verifyLeft.Children.Add(YYIcons.Make("waveform-ecg", 13.5, ThemeHelper.Brush(p.Ok)));
            verifyLeft.Children.Add(new TextBlock { Text = "检查并添加", FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(p.Ok) });

            var verifyGrid = new Grid { Padding = new Thickness(12, 9, 12, 9) };
            verifyGrid.Children.Add(verifyLeft);
            verifyGrid.Children.Add(statusRight);

            var verifyBtn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(9),
                UseSystemFocusVisuals = false,
                Content = verifyGrid,
            };

            async Task RunVerifyAndConnectAsync()
            {
                if (isLocal && urlBox is not null)
                {
                    if (customNameField is not null && !string.IsNullOrWhiteSpace(customNameField.Text))
                        _library.Prefs.CloudVoiceCustomNames["local"] = customNameField.Text.Trim();
                    _library.Prefs.CloudVoiceBaseUrls[pk] = string.IsNullOrWhiteSpace(urlBox.Text) ? CloudVoiceService.DefaultLocalUrl(prov) : urlBox.Text.Trim();
                    _library.Prefs.CloudVoiceDismissed.Remove(pk);
                    _library.SavePrefs();
                }
                if (prov == CloudTtsProvider.Volcengine && extraBox is not null)
                    _library.Secrets.Set("tts.volcengine.appid", extraBox.Text.Trim());
                if (prov == CloudTtsProvider.Azure && extraBox is not null)
                {
                    _library.Prefs.CloudVoiceAzureRegion = extraBox.Text.Trim();
                    _library.SavePrefs();
                }
                if (keyBox is not null)
                    _library.CloudVoices.SetApiKey(prov, keyBox.Password.Trim());

                verifyBtn.IsEnabled = false;
                statusRight.Text = "正在验证…";
                statusRight.Foreground = ThemeHelper.Brush(p.T2);
                var (ok, _, msg) = await _library.CloudVoices.RefreshVoicesAsync(prov);
                verifyBtn.IsEnabled = true;
                if (ok)
                {
                    SetFocusedVoiceSource(pk);
                    _voiceSourceConfigOpen = false;
                    App.CurrentApp.BroadcastVoiceChanged();
                    dlgRef?.Hide();
                    RenderDynamicPage("listening");
                    RenderInspector("listening");
                    ShowFlashBanner($"✓ {msg}");
                }
                else
                {
                    statusRight.Text = msg;
                    statusRight.Foreground = ThemeHelper.Brush(p.Danger);
                }
            }

            void SyncVerifyReady()
            {
                bool ready = isLocal
                    ? true
                    : !string.IsNullOrWhiteSpace(keyBox?.Password);
                verifyBtn.Opacity = ready ? 1.0 : 0.62;
                if (statusRight.Foreground != ThemeHelper.Brush(p.Danger))
                    statusRight.Text = ready ? "点此验证" : (isLocal ? "探测端口" : "先填 Key");
            }
            if (urlBox is not null) urlBox.TextChanged += (_, _) => SyncVerifyReady();
            if (keyBox is not null) keyBox.PasswordChanged += (_, _) => SyncVerifyReady();
            SyncVerifyReady();

            verifyBtn.Click += async (_, _) => await RunVerifyAndConnectAsync();
            cardStack.Children.Add(verifyBtn);

            // Explicit "复用「书伴」的 Key" pill button when a matching AI key exists
            if (!isLocal && keyBox is not null && _library.CloudVoices.GetCompanionSharedKey(prov) is { Length: > 0 } sharedKey)
            {
                var reusePill = new Button
                {
                    Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Height = 34,
                    Content = $"{L10n.T("复用「书伴」的 Key")}（{MaskSecretKey(sharedKey)}）",
                };
                reusePill.Click += async (_, _) =>
                {
                    keyBox.Password = sharedKey;
                    await RunVerifyAndConnectAsync();
                };
                wrap.Children.Add(reusePill);
            }

            wrap.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(12),
                Background = ThemeHelper.Brush(p.Card),
                BorderBrush = ThemeHelper.Brush(p.Line),
                BorderThickness = new Thickness(0.75),
                Child = cardStack,
            });

            var (portalUrl, portalTrail) = ProviderKeyPortal(prov);
            if (!string.IsNullOrEmpty(portalUrl))
            {
                var getSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
                getSp.Children.Add(new TextBlock { Text = "↗", FontSize = 12.5, Foreground = ThemeHelper.Brush(p.T2), VerticalAlignment = VerticalAlignment.Center });
                getSp.Children.Add(new TextBlock { Text = $"获取 {dispName} Key", FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1), VerticalAlignment = VerticalAlignment.Center });
                var getKeyCardBtn = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Height = 42,
                    Padding = new Thickness(14, 0, 14, 0),
                    CornerRadius = new CornerRadius(12),
                    Background = ThemeHelper.Brush(p.Card),
                    BorderBrush = ThemeHelper.Brush(p.Line),
                    BorderThickness = new Thickness(0.75),
                    UseSystemFocusVisuals = false,
                    Content = getSp,
                };
                getKeyCardBtn.Click += (_, _) => _ = Launcher.LaunchUriAsync(new Uri(portalUrl));
                wrap.Children.Add(getKeyCardBtn);
            }

            string footText = !string.IsNullOrEmpty(portalTrail)
                ? $"{portalTrail} API Key 仅保存在本机，调用费用由服务商直接结算。"
                : "连接成功后自动出现在「声音来源」列表，已朗读句子自动缓存在本机。";
            wrap.Children.Add(new TextBlock { Text = footText, FontSize = 11.5, LineHeight = 18, Foreground = ThemeHelper.Brush(p.T3), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 0, 4, 0) });

            var backBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
                Content = "‹ 返回全部声音来源",
                FontSize = 11.5,
                Foreground = ThemeHelper.Brush(p.T3),
                Padding = new Thickness(4, 2, 6, 2),
            };
            backBtn.Click += (_, _) => RenderPickerStep();
            wrap.Children.Add(backBtn);

            L10n.ApplyTree(wrap);
            stageHost.Children.Add(wrap);
            YYChrome.AnimateEntrance(wrap, fromY: 6f, durationMs: 140);
        }

        RenderPickerStep();
        dlgRef = YYChrome.CreateMacSheet(RootGrid.XamlRoot, IsDark, 480, stageHost, tint: _library.Prefs.Tint);
        await dlgRef.ShowSafeAsync(_library.Prefs.Tint);
    }

#if DEBUG
    internal void OpenDebugSheet(string kind)
    {
        if (kind == "add-provider") _ = ShowAddProviderSheetAsync();
        else if (kind == "add-voice") _ = ShowAddVoiceSourceDialogAsync();
        else if (kind == "fontbook") _ = ShowSettingsFontBookAsync((_, _, _) => { });
        else if (kind == "voice-source")
        {
            SetFocusedVoiceSource("edge");
            RenderDynamicPage("listening");
            RenderInspector("listening");
        }
    }
#endif

    private static string[] GetRecommendedModelsForProvider(string providerId, string defaultModel) => providerId switch
    {
        "deepseek" => ["deepseek-chat", "deepseek-reasoner"],
        "claude" => ["claude-sonnet-4-6", "claude-opus-4-6", "claude-sonnet-4-5", "claude-haiku-4-5"],
        "openai" => ["gpt-4.1", "gpt-4.1-mini", "gpt-5-mini", "gpt-5", "gpt-4o", "gpt-4o-mini"],
        "gemini" => ["gemini-2.5-flash", "gemini-2.5-pro"],
        "openrouter" => ["openrouter/auto", "anthropic/claude-sonnet-4.6", "anthropic/claude-sonnet-4.5", "google/gemini-2.5-flash", "deepseek/deepseek-chat"],
        "dashscope" => ["qwen-plus", "qwen-max", "qwen-turbo"],
        "moonshot" => ["kimi-k2-turbo-preview", "moonshot-v1-32k"],
        "zhipu" => ["glm-4.6", "glm-4-flash"],
        "volcengine" => ["doubao-seed-1-6-250615"],
        "siliconflow" => ["deepseek-ai/DeepSeek-V3", "Qwen/Qwen2.5-72B-Instruct"],
        "ollama" => ["qwen2.5:7b", "llama3.1:8b", "deepseek-r1:7b"],
        "lmstudio" => ["qwen2.5-7b-instruct", "llama-3.1-8b-instruct"],
        _ => string.IsNullOrEmpty(defaultModel) ? [] : [defaultModel],
    };

    private async Task ShowAddProviderSheetAsync()
    {
        var p = CurrentPalette;
        var tint = CurrentTint;
        ContentDialog? dlgRef = null;
        string? connectedProviderId = null;
        bool sheetClosed = false;
        CancellationTokenSource? sheetOrAuthCts = null;
        var stageHost = new Grid();

        void CancelSheetOrAuth()
        {
            try { sheetOrAuthCts?.Cancel(); sheetOrAuthCts?.Dispose(); } catch { }
            sheetOrAuthCts = null;
        }

        void CommitConnectedProvider(string pid, string? customName, string baseUrl, string modelId, string apiKey)
        {
            if (!string.IsNullOrWhiteSpace(customName) && pid.StartsWith("custom.", StringComparison.OrdinalIgnoreCase))
                _library.Prefs.AiCustomProviderNames[pid] = customName.Trim();
            if (!string.IsNullOrWhiteSpace(baseUrl))
                _library.Prefs.AiProviderUrls[pid] = baseUrl.Trim();
            if (!string.IsNullOrWhiteSpace(modelId))
            {
                _library.Prefs.AiProviderModels[pid] = modelId.Trim();
                _library.Companion.RecordModelUsed(pid, modelId.Trim());
            }
            if (!string.IsNullOrEmpty(apiKey))
                _library.Secrets.Set($"ai.key.{pid}", apiKey.Trim());

            if (!_library.Prefs.ConfiguredAiProviders.Contains(pid, StringComparer.OrdinalIgnoreCase))
                _library.Prefs.ConfiguredAiProviders.Add(pid);

            _newlyAddedCompanionIds.Remove(pid);
            string prevProv = _library.Prefs.AiProvider;
            string prevTitle = string.IsNullOrEmpty(prevProv) ? L10n.T("未设置") : ShortProviderTitle(prevProv, prevProv);
            ActivateCompanionProvider(pid);
            string newTitle = ShortProviderTitle(pid, pid);
            RecordPrefChange(
                nameof(DevicePrefs.AiProvider),
                "书伴服务",
                newTitle,
                prevTitle,
                prevProv,
                pid,
                undo: () =>
                {
                    if (!string.IsNullOrEmpty(prevProv)) ActivateCompanionProvider(prevProv);
                    if (_activePage == "companion") { RenderDynamicPage("companion"); RenderInspector("companion"); }
                },
                redo: () =>
                {
                    ActivateCompanionProvider(pid);
                    if (_activePage == "companion") { RenderDynamicPage("companion"); RenderInspector("companion"); }
                },
                pageOverride: "companion");

            connectedProviderId = pid;
            _focusedCompanionId = pid;
            _companionPickingModel = false;
            _companionEditingKey = false;
            dlgRef?.Hide();
        }

        void RenderPickerStep()
        {
            CancelSheetOrAuth();
            stageHost.Children.Clear();

            var searchRow = new Grid
            {
                Height = 36,
                Padding = new Thickness(12, 0, 12, 0),
                CornerRadius = new CornerRadius(10),
                Background = ThemeHelper.Brush(p.Inset),
                ColumnSpacing = 8,
            };
            searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            searchRow.Children.Add(YYIcons.Make("search", 13.5, ThemeHelper.Brush(p.T3)));
            var searchBox = new TextBox
            {
                PlaceholderText = "搜索服务商，比如 千问、Kimi、magpie",
                BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(Colors.Transparent),
                FontSize = 12.5,
                VerticalAlignment = VerticalAlignment.Center,
                Padding = new Thickness(0, 6, 0, 6),
            };
            YYChrome.StripTextBoxChrome(searchBox);
            AutomationProperties.SetName(searchBox, L10n.T("搜索服务商"));
            Grid.SetColumn(searchBox, 1);
            searchRow.Children.Add(searchBox);

            var groupsHost = new StackPanel { Spacing = 16 };

            void RebuildGroups(string rawQuery)
            {
                groupsHost.Children.Clear();
                string q = rawQuery.Trim().Replace(" ", "").Replace("-", "").Replace("_", "").ToLowerInvariant();
                var catalog = CompanionClient.Presets
                    .Where(pr => pr.Id != "custom" && (string.IsNullOrEmpty(q)
                        || $"{pr.Id}{pr.Name}{pr.Aliases}".Replace(" ", "").ToLowerInvariant().Contains(q)))
                    .ToArray();

                foreach (var (grpKey, grpTitle) in new[]
                {
                    ("signIn", "登录账号"),
                    ("key", "API Key"),
                    ("local", "本机和局域网"),
                })
                {
                    var list = catalog.Where(x => x.Group == grpKey).ToArray();
                    if (list.Length == 0) continue;

                    var sec = new StackPanel { Spacing = 6 };
                    sec.Children.Add(new TextBlock
                    {
                        Text = grpTitle,
                        FontSize = 11.5,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        Foreground = ThemeHelper.Brush(p.T3),
                        Margin = new Thickness(4, 0, 0, 0),
                    });

                    var grid = new Grid { ColumnSpacing = 8, RowSpacing = 6 };
                    for (int c = 0; c < 3; c++)
                        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    int rows = (list.Length + 2) / 3;
                    for (int r = 0; r < rows; r++)
                        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                    for (int i = 0; i < list.Length; i++)
                    {
                        var pr = list[i];
                        bool already = _library.Prefs.ConfiguredAiProviders.Contains(pr.Id, StringComparer.OrdinalIgnoreCase);
                        var row = new Grid { ColumnSpacing = 9 };
                        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
                        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                        row.Children.Add(YYChrome.MakeBareServiceIcon(pr.Id, IsDark, 20));

                        var lbl = new TextBlock
                        {
                            Text = already ? $"{pr.Name} ✓" : pr.Name,
                            FontSize = 13,
                            Foreground = ThemeHelper.Brush(already ? p.T3 : p.T1),
                            VerticalAlignment = VerticalAlignment.Center,
                            MaxLines = 1,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                        };
                        Grid.SetColumn(lbl, 1);
                        row.Children.Add(lbl);

                        var btn = new Button
                        {
                            HorizontalAlignment = HorizontalAlignment.Stretch,
                            HorizontalContentAlignment = HorizontalAlignment.Stretch,
                            Padding = new Thickness(6, 7, 6, 7),
                            CornerRadius = new CornerRadius(8),
                            Background = new SolidColorBrush(Colors.Transparent),
                            BorderThickness = new Thickness(0),
                            UseSystemFocusVisuals = false,
                            Content = row,
                        };
                        if (!string.IsNullOrEmpty(pr.Blurb))
                            ToolTipService.SetToolTip(btn, pr.Blurb);

                        string pid = pr.Id;
                        btn.Click += (_, _) => RenderConnectStep(pid, isCustomNew: false);
                        Grid.SetRow(btn, i / 3);
                        Grid.SetColumn(btn, i % 3);
                        grid.Children.Add(btn);
                    }
                    sec.Children.Add(grid);
                    groupsHost.Children.Add(sec);
                }

                if (groupsHost.Children.Count == 0)
                {
                    groupsHost.Children.Add(new TextBlock
                    {
                        Text = "没找到。点下方「自己添加」，填上接口地址就行",
                        FontSize = 12,
                        Foreground = ThemeHelper.Brush(p.T3),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 28, 0, 28),
                    });
                }
            }

            searchBox.TextChanged += (_, _) => RebuildGroups(searchBox.Text);
            RebuildGroups("");

            var customRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            customRow.Children.Add(YYIcons.Make("plus", 13, ThemeHelper.Brush(p.T2)));
            customRow.Children.Add(new TextBlock { Text = "自己添加", FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1), VerticalAlignment = VerticalAlignment.Center });
            customRow.Children.Add(new TextBlock { Text = "任何 OpenAI 兼容接口", FontSize = 12.5, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Center });

            var customBtn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(22, 12, 22, 12),
                CornerRadius = new CornerRadius(0),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                UseSystemFocusVisuals = false,
                Content = customRow,
            };
            customBtn.Click += (_, _) =>
            {
                string cid = "custom." + Guid.NewGuid().ToString("N")[..8];
                RenderConnectStep(cid, isCustomNew: true);
            };

            var topPad = new StackPanel
            {
                Spacing = 14,
                Padding = new Thickness(22, 20, 22, 12),
                Children =
                {
                    YYChrome.MakeSheetHeader("添加服务商", "书伴", () => dlgRef?.Hide(), IsDark, titleSize: 18),
                    searchRow,
                    new ScrollViewer { MaxHeight = 330, Content = groupsHost },
                },
            };

            var outer = new StackPanel { Spacing = 0 };
            outer.Children.Add(topPad);
            outer.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line) });
            outer.Children.Add(customBtn);
            L10n.ApplyTree(outer);
            stageHost.Children.Add(outer);
        }

        void RenderConnectStep(string pid, bool isCustomNew)
        {
            CancelSheetOrAuth();
            stageHost.Children.Clear();
            var preset = ResolveCompanionPreset(pid);
            bool isLocal = preset.Group == "local";
            string dispName = isCustomNew ? "自定义兼容服务" : ShortProviderTitle(pid, preset.Name);

            var wrap = new StackPanel { Spacing = 14, Padding = new Thickness(22, 20, 22, 18) };

            var headRow = new Grid { ColumnSpacing = 12 };
            headRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            headRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headRow.Children.Add(MakeServiceLogoTile(pid, 36));

            var headMeta = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            headMeta.Children.Add(new TextBlock
            {
                Text = $"连接 {dispName}",
                FontSize = 16.5,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = ThemeHelper.Brush(p.T1),
            });
            headMeta.Children.Add(new TextBlock
            {
                Text = string.IsNullOrEmpty(preset.Blurb) ? "验证通过后才会添加到你的书伴服务列表" : preset.Blurb,
                FontSize = 11.5,
                Foreground = ThemeHelper.Brush(p.T3),
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            Grid.SetColumn(headMeta, 1);
            headRow.Children.Add(headMeta);

            var closeBtn = YYChrome.MakeSheetCloseButton(() => { CancelSheetOrAuth(); dlgRef?.Hide(); }, IsDark);
            Grid.SetColumn(closeBtn, 2);
            headRow.Children.Add(closeBtn);
            wrap.Children.Add(headRow);

            var formStack = new StackPanel { Spacing = 0 };
            void AddFormRow(string label, FrameworkElement field, bool withDivider)
            {
                var g = new Grid { Padding = new Thickness(14, 7, 14, 7), ColumnSpacing = 10, MinHeight = 38 };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.Children.Add(new TextBlock
                {
                    Text = label,
                    FontSize = 12.5,
                    Foreground = ThemeHelper.Brush(p.T2),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                Grid.SetColumn(field, 1);
                g.Children.Add(field);
                formStack.Children.Add(g);
                if (withDivider)
                    formStack.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(14, 0, 14, 0) });
            }

            TextBox? customNameBox = null;
            if (isCustomNew || pid.StartsWith("custom.", StringComparison.OrdinalIgnoreCase))
            {
                customNameBox = new TextBox
                {
                    Text = _library.Prefs.AiCustomProviderNames.TryGetValue(pid, out var cn) && !string.IsNullOrWhiteSpace(cn) ? cn : "自定义服务",
                    PlaceholderText = "例如：公司网关",
                    BorderThickness = new Thickness(0),
                    Background = new SolidColorBrush(Colors.Transparent),
                    FontSize = 12.5,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                YYChrome.StripTextBoxChrome(customNameBox);
                AutomationProperties.SetName(customNameBox, L10n.T("服务名称"));
                AddFormRow("服务名称", customNameBox, withDivider: true);
            }

            var keyBox = new PasswordBox
            {
                Password = _library.Secrets.Get($"ai.key.{pid}"),
                PlaceholderText = isLocal ? "本机服务一般可留空" : (string.IsNullOrEmpty(preset.KeyHint) ? "sk-..." : preset.KeyHint),
                BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(Colors.Transparent),
                FontSize = 12.5,
                VerticalAlignment = VerticalAlignment.Center,
            };
            YYChrome.StripPasswordBoxChrome(keyBox);
            AutomationProperties.SetName(keyBox, "API Key");
            if (!isLocal || isCustomNew)
                AddFormRow("API Key", keyBox, withDivider: true);

            var urlBox = new TextBox
            {
                Text = GetProviderUrl(pid, preset.BaseUrl),
                PlaceholderText = string.IsNullOrEmpty(preset.BaseUrl) ? "https://.../v1" : preset.BaseUrl,
                BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(Colors.Transparent),
                FontSize = 12,
                FontFamily = new FontFamily(YYFontFamily.Mono.WindowsFamily()),
                VerticalAlignment = VerticalAlignment.Center,
            };
            YYChrome.StripTextBoxChrome(urlBox);
            AutomationProperties.SetName(urlBox, L10n.T("接口地址"));
            AddFormRow("接口地址", urlBox, withDivider: true);

            var modelRowGrid = new Grid { ColumnSpacing = 6 };
            modelRowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            modelRowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var modelBox = new TextBox
            {
                Text = GetProviderModel(pid, preset.DefaultModel),
                PlaceholderText = string.IsNullOrEmpty(preset.DefaultModel) ? "例如 gpt-4.1-mini" : preset.DefaultModel,
                BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(Colors.Transparent),
                FontSize = 12,
                FontFamily = new FontFamily(YYFontFamily.Mono.WindowsFamily()),
                VerticalAlignment = VerticalAlignment.Center,
            };
            YYChrome.StripTextBoxChrome(modelBox);
            AutomationProperties.SetName(modelBox, L10n.T("模型"));
            modelRowGrid.Children.Add(modelBox);

            var modelListBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
                Content = "列表 ▾",
                Height = 24,
                MinHeight = 24,
                Padding = new Thickness(8, 0, 8, 0),
                FontSize = 11,
            };
            modelListBtn.Click += async (_, _) =>
            {
                modelListBtn.IsEnabled = false;
                modelListBtn.Content = L10n.T("拉取…");
                var menu = new MenuFlyout();
                var candidates = new List<string>();
                foreach (var r in GetRecommendedModelsForProvider(pid, preset.DefaultModel))
                    if (!candidates.Contains(r, StringComparer.OrdinalIgnoreCase)) candidates.Add(r);
                try
                {
                    var remote = await _library.Companion.ListModelsAsync(pid, urlBox.Text.Trim(), keyBox.Password.Trim());
                    if (_closed || sheetClosed) return;
                    if (remote.Count > 0)
                    {
                        _cachedCompanionModels[pid] = remote;
                        foreach (var rm in remote.Take(30))
                            if (!candidates.Contains(rm, StringComparer.OrdinalIgnoreCase)) candidates.Add(rm);
                    }
                }
                catch { }
                finally
                {
                    if (!_closed && !sheetClosed)
                    {
                        modelListBtn.IsEnabled = true;
                        modelListBtn.Content = L10n.T("列表 ▾");
                    }
                }

                if (_closed || sheetClosed) return;
                if (candidates.Count == 0)
                {
                    menu.Items.Add(new MenuFlyoutItem { Text = L10n.T("未拉取到模型，请直接输入模型 ID"), IsEnabled = false });
                }
                else
                {
                    foreach (var mid in candidates.Take(24))
                    {
                        string capM = mid;
                        string friendly = FormatFriendlyModelName(capM);
                        string label = string.Equals(friendly, capM, StringComparison.OrdinalIgnoreCase)
                            ? capM
                            : $"{friendly} ({capM})";
                        var mi = new MenuFlyoutItem { Text = L10n.T(string.Equals(modelBox.Text.Trim(), capM, StringComparison.OrdinalIgnoreCase) ? $"✓ {label}" : label) };
                        mi.Click += (_, _) => modelBox.Text = capM;
                        menu.Items.Add(mi);
                    }
                }
                menu.ShowAt(modelListBtn);
            };
            Grid.SetColumn(modelListBtn, 1);
            modelRowGrid.Children.Add(modelListBtn);
            AddFormRow("模型", modelRowGrid, withDivider: false);

            var statusNote = new TextBlock
            {
                FontSize = 11.5,
                Foreground = ThemeHelper.Brush(p.T3),
                TextWrapping = TextWrapping.Wrap,
                Visibility = Visibility.Collapsed,
            };

            var connectBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYPrimaryPillButtonStyle"],
                Content = "检查并添加",
                MinWidth = 112,
            };

            var manualSection = new StackPanel { Spacing = 10 };
            manualSection.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(12),
                Background = ThemeHelper.Brush(p.Card),
                BorderBrush = ThemeHelper.Brush(p.Line),
                BorderThickness = new Thickness(0.75),
                Child = formStack,
            });
            if (!string.IsNullOrEmpty(preset.KeyUrl))
            {
                string kurl = preset.KeyUrl;
                manualSection.Children.Add(new HyperlinkButton
                {
                    Content = $"去 {preset.Name} 控制台获取 API Key ↗",
                    FontSize = 11.5,
                    Padding = new Thickness(2, 0, 2, 0),
                    NavigateUri = new Uri(kurl),
                });
            }

            if (pid == "openrouter")
            {
                var orRow = new Grid { ColumnSpacing = 8 };
                orRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                orRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var orBtn = new Button
                {
                    Style = (Style)Application.Current.Resources["YYPrimaryPillButtonStyle"],
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Height = 38,
                    Content = "用 OpenRouter 账号一键登录并添加…",
                };
                var orCancelBtn = new Button
                {
                    Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
                    Height = 38,
                    Content = "取消",
                    Visibility = Visibility.Collapsed,
                };
                orCancelBtn.Click += (_, _) => CancelSheetOrAuth();
                orBtn.Click += async (_, _) =>
                {
                    CancelSheetOrAuth();
                    var cts = new CancellationTokenSource();
                    sheetOrAuthCts = cts;
                    orBtn.IsEnabled = false;
                    orBtn.Content = L10n.T("等待浏览器授权…");
                    orCancelBtn.Visibility = Visibility.Visible;
                    statusNote.Text = L10n.T("已在浏览器打开授权页，登录完成后自动回到语阅…");
                    statusNote.Foreground = ThemeHelper.Brush(p.T3);
                    statusNote.Visibility = Visibility.Visible;
                    try
                    {
                        string gotKey = await CompanionClient.SignInWithOpenRouterAsync(cts.Token);
                        if (_closed || sheetClosed) return;
                        CommitConnectedProvider("openrouter", null, urlBox.Text.Trim(), modelBox.Text.Trim(), gotKey);
                    }
                    catch (OperationCanceledException)
                    {
                        if (!_closed && !sheetClosed)
                        {
                            statusNote.Text = L10n.T("已取消授权");
                            statusNote.Foreground = ThemeHelper.Brush(p.T3);
                        }
                    }
                    catch (Exception ex)
                    {
                        if (!_closed && !sheetClosed)
                        {
                            statusNote.Text = ex.Message;
                            statusNote.Foreground = ThemeHelper.Brush(p.Danger);
                        }
                    }
                    finally
                    {
                        if (ReferenceEquals(sheetOrAuthCts, cts))
                        {
                            try { cts.Dispose(); } catch { }
                            sheetOrAuthCts = null;
                        }
                        if (!_closed && !sheetClosed)
                        {
                            orBtn.IsEnabled = true;
                            orBtn.Content = L10n.T("用 OpenRouter 账号一键登录并添加…");
                            orCancelBtn.Visibility = Visibility.Collapsed;
                        }
                    }
                };
                orRow.Children.Add(orBtn);
                Grid.SetColumn(orCancelBtn, 1);
                orRow.Children.Add(orCancelBtn);
                wrap.Children.Add(orRow);

                bool manualExpanded = !string.IsNullOrEmpty(keyBox.Password);
                manualSection.Visibility = manualExpanded ? Visibility.Visible : Visibility.Collapsed;
                connectBtn.Visibility = manualExpanded ? Visibility.Visible : Visibility.Collapsed;

                var disclosureBtn = new Button
                {
                    Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
                    Content = manualExpanded ? "我有 Key，直接填 ▴" : "我有 Key，直接填 ▾",
                    FontSize = 12,
                    Foreground = ThemeHelper.Brush(p.T2),
                    Padding = new Thickness(2, 2, 6, 2),
                };
                disclosureBtn.Click += (_, _) =>
                {
                    manualExpanded = !manualExpanded;
                    manualSection.Visibility = manualExpanded ? Visibility.Visible : Visibility.Collapsed;
                    connectBtn.Visibility = manualExpanded ? Visibility.Visible : Visibility.Collapsed;
                    disclosureBtn.Content = L10n.T(manualExpanded ? "我有 Key，直接填 ▴" : "我有 Key，直接填 ▾");
                    if (manualExpanded)
                        keyBox.Focus(FocusState.Programmatic);
                };
                wrap.Children.Add(disclosureBtn);
            }

            wrap.Children.Add(manualSection);
            wrap.Children.Add(statusNote);

            var footRow = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 2, 0, 0) };
            footRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            footRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var backBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
                Content = "‹ 返回列表",
                FontSize = 12,
                Foreground = ThemeHelper.Brush(p.T2),
            };
            backBtn.Click += (_, _) => { CancelSheetOrAuth(); RenderPickerStep(); };
            footRow.Children.Add(backBtn);

            connectBtn.Click += async (_, _) =>
            {
                string testBase = urlBox.Text.Trim();
                if (string.IsNullOrEmpty(testBase)) testBase = preset.BaseUrl;
                string testModel = modelBox.Text.Trim();
                if (string.IsNullOrEmpty(testModel)) testModel = preset.DefaultModel;
                string trimmedKey = keyBox.Password.Trim();

                if (!isLocal && string.IsNullOrEmpty(trimmedKey))
                {
                    statusNote.Text = L10n.T("请先填写 API Key");
                    statusNote.Foreground = ThemeHelper.Brush(p.Danger);
                    statusNote.Visibility = Visibility.Visible;
                    return;
                }
                if (string.IsNullOrEmpty(testBase))
                {
                    statusNote.Text = L10n.T("请填写接口地址");
                    statusNote.Foreground = ThemeHelper.Brush(p.Danger);
                    statusNote.Visibility = Visibility.Visible;
                    return;
                }

                connectBtn.IsEnabled = false;
                backBtn.IsEnabled = false;
                connectBtn.Content = L10n.T("正在检查…");
                statusNote.Text = L10n.T("正在检查接口与模型连通性…");
                statusNote.Foreground = ThemeHelper.Brush(p.T3);
                statusNote.Visibility = Visibility.Visible;

                var (ok, msg) = await _library.Companion.TestProviderAsync(pid, testBase, testModel, trimmedKey);
                if (_closed || sheetClosed) return;
                if (ok)
                {
                    CommitConnectedProvider(pid, customNameBox?.Text, testBase, testModel, trimmedKey);
                    return;
                }

                statusNote.Text = msg;
                statusNote.Foreground = ThemeHelper.Brush(p.Danger);
                connectBtn.IsEnabled = true;
                backBtn.IsEnabled = true;
                connectBtn.Content = L10n.T("检查并添加");
            };
            Grid.SetColumn(connectBtn, 2);
            footRow.Children.Add(connectBtn);
            wrap.Children.Add(footRow);

            L10n.ApplyTree(wrap);
            stageHost.Children.Add(wrap);
            YYChrome.AnimateEntrance(wrap, fromY: 6f, durationMs: 140);
        }

        RenderPickerStep();
        dlgRef = YYChrome.CreateMacSheet(RootGrid.XamlRoot, IsDark, 480, stageHost, tint: _library.Prefs.Tint);
        dlgRef.Closed += (_, _) =>
        {
            sheetClosed = true;
            CancelSheetOrAuth();
        };
        await dlgRef.ShowSafeAsync(_library.Prefs.Tint);
        if (!string.IsNullOrEmpty(connectedProviderId))
        {
            _focusedCompanionId = connectedProviderId;
            _companionPickingModel = false;
            _companionEditingKey = false;
            RenderDynamicPage("companion");
            RenderInspector("companion");
            L10n.ApplyTree(DynamicPageHost);
            L10n.ApplyTree(InspectorHost);
        }
    }

    private void BuildCompanionInspector(YYPalette p, YYTintColors tint)
    {
        var preset = ResolveCompanionPreset(_focusedCompanionId);
        bool inUse = _library.Prefs.AiProvider == preset.Id;
        string curModel = GetProviderModel(preset.Id, preset.DefaultModel);
        var monoFamily = new FontFamily(YYFontFamily.Mono.WindowsFamily());

        // ModelList 子页钻取（1:1 MacCompanionSettings.swift:296-543 + GET /models 动态拉取 + 友好模型名）
        if (_companionPickingModel)
        {
            var backRow = new Grid();
            var backLeft = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var backBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "‹ 返回", Height = 25, MinHeight = 25, Padding = new Thickness(8, 0, 10, 0) };
            backBtn.Click += (_, _) => { _companionPickingModel = false; RenderInspector("companion"); };
            backLeft.Children.Add(backBtn);
            backLeft.Children.Add(new TextBlock { Text = "选择模型", FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1), VerticalAlignment = VerticalAlignment.Center });
            backRow.Children.Add(backLeft);

            var fetchBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "↻ 拉取列表", Height = 25, MinHeight = 25, Padding = new Thickness(8, 0, 8, 0), HorizontalAlignment = HorizontalAlignment.Right, FontSize = 11 };
            backRow.Children.Add(fetchBtn);
            InspectorHost.Children.Add(backRow);

            string[] recommended = GetRecommendedModelsForProvider(preset.Id, preset.DefaultModel);

            var filterBox = new TextBox { PlaceholderText = "筛选模型名称、ID 或厂商…", FontSize = 11.5 };
            var makerStrip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            var makerScroll = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollMode = ScrollMode.Auto,
                VerticalScrollMode = ScrollMode.Disabled,
                Content = makerStrip,
            };
            var mStack = new StackPanel { Spacing = 4 };
            string selectedMaker = "全部";

            List<string> BuildPool()
            {
                var merged = new List<string>();
                foreach (var r in recommended)
                    if (!merged.Contains(r, StringComparer.OrdinalIgnoreCase)) merged.Add(r);
                foreach (var persisted in _library.Companion.GetCachedModels(preset.Id))
                    if (!merged.Contains(persisted, StringComparer.OrdinalIgnoreCase)) merged.Add(persisted);
                if (_cachedCompanionModels.TryGetValue(preset.Id, out var remoteList))
                {
                    foreach (var rm in remoteList)
                        if (!merged.Contains(rm, StringComparer.OrdinalIgnoreCase)) merged.Add(rm);
                }
                return merged;
            }

            void SelectModelAndClose(string nextM)
            {
                string prevM = curModel;
                _library.Prefs.AiProviderModels[preset.Id] = nextM;
                _library.Companion.RecordModelUsed(preset.Id, nextM);
                if (inUse) ActivateCompanionProvider(preset.Id);
                else _library.SavePrefs();
                RecordChange(
                    "书伴模型",
                    FormatFriendlyModelName(nextM),
                    undo: () =>
                    {
                        _library.Prefs.AiProviderModels[preset.Id] = prevM;
                        if (_library.Prefs.AiProvider == preset.Id) ActivateCompanionProvider(preset.Id);
                        else _library.SavePrefs();
                        if (_activePage == "companion") { RenderDynamicPage("companion"); RenderInspector("companion"); }
                    },
                    redo: () =>
                    {
                        _library.Prefs.AiProviderModels[preset.Id] = nextM;
                        if (_library.Prefs.AiProvider == preset.Id) ActivateCompanionProvider(preset.Id);
                        else _library.SavePrefs();
                        if (_activePage == "companion") { RenderDynamicPage("companion"); RenderInspector("companion"); }
                    },
                    oldValue: FormatFriendlyModelName(prevM));
                _companionPickingModel = false;
                RenderDynamicPage("companion");
                RenderInspector("companion");
            }

            void PopulateModels(string filter)
            {
                mStack.Children.Clear();
                makerStrip.Children.Clear();
                string fq = filter.Trim();
                var pool = BuildPool();

                var distinctMakers = pool.Select(CompanionClient.InferModelMaker).Distinct(StringComparer.Ordinal).ToList();
                bool hasStars = pool.Any(m => _library.Companion.IsModelStarred(preset.Id, m));
                var makerChoices = new List<string> { "全部" };
                if (hasStars) makerChoices.Add("★ 收藏");
                if (distinctMakers.Count >= 2) makerChoices.AddRange(distinctMakers);
                if (!makerChoices.Contains(selectedMaker)) selectedMaker = "全部";

                makerScroll.Visibility = makerChoices.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
                foreach (string mk in makerChoices)
                {
                    string capMk = mk;
                    bool onMk = selectedMaker == capMk;
                    var pill = new Button
                    {
                        Height = 24,
                        MinHeight = 24,
                        Padding = new Thickness(9, 0, 9, 0),
                        CornerRadius = new CornerRadius(12),
                        FontSize = 11,
                        FontWeight = onMk ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                        Background = ThemeHelper.Brush(onMk ? tint.AccentSoft : p.Inset),
                        Foreground = ThemeHelper.Brush(onMk ? tint.Accent : p.T2),
                        BorderThickness = new Thickness(0),
                        UseSystemFocusVisuals = false,
                        Content = capMk,
                    };
                    pill.Click += (_, _) => { selectedMaker = capMk; PopulateModels(filterBox.Text); };
                    makerStrip.Children.Add(pill);
                }

                UIElement BuildModelRow(string mid)
                {
                    bool on = string.Equals(curModel, mid, StringComparison.OrdinalIgnoreCase);
                    bool starred = _library.Companion.IsModelStarred(preset.Id, mid);
                    string? badge = CompanionClient.InferRouteBadge(preset.Id, mid);
                    string friendly = FormatFriendlyModelName(mid);
                    bool showSubId = !string.Equals(friendly, mid, StringComparison.OrdinalIgnoreCase);

                    var rowGrid = new Grid { ColumnSpacing = 4 };
                    rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var btnInner = new Grid { ColumnSpacing = 6 };
                    btnInner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    btnInner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var titleStack = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
                    titleStack.Children.Add(new TextBlock
                    {
                        Text = on ? $"✓ {friendly}" : friendly,
                        FontSize = 12,
                        FontWeight = on ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Medium,
                        Foreground = ThemeHelper.Brush(on ? tint.Accent : p.T1),
                        MaxLines = 1,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    });
                    if (showSubId)
                    {
                        titleStack.Children.Add(new TextBlock
                        {
                            Text = mid,
                            FontFamily = monoFamily,
                            FontSize = 10,
                            Foreground = ThemeHelper.Brush(p.T3),
                            MaxLines = 1,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                        });
                    }
                    btnInner.Children.Add(titleStack);

                    if (!string.IsNullOrEmpty(badge))
                    {
                        var tag = MakeStatusPill(badge, badge is "免费路由" or "本机" ? p.Ok : p.T3);
                        tag.VerticalAlignment = VerticalAlignment.Center;
                        Grid.SetColumn(tag, 1);
                        btnInner.Children.Add(tag);
                    }

                    var mb = new Button
                    {
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        Padding = new Thickness(9, 6, 8, 6),
                        CornerRadius = new CornerRadius(9),
                        BorderThickness = new Thickness(1),
                        Background = ThemeHelper.Brush(on ? tint.Soft : p.Card),
                        BorderBrush = ThemeHelper.Brush(on ? tint.Edge : p.Line),
                        UseSystemFocusVisuals = false,
                        Content = btnInner,
                    };
                    string nextM = mid;
                    mb.Click += (_, _) => SelectModelAndClose(nextM);
                    rowGrid.Children.Add(mb);

                    var starBtn = new Button
                    {
                        Width = 26,
                        Height = 26,
                        Padding = new Thickness(0),
                        CornerRadius = new CornerRadius(8),
                        Background = new SolidColorBrush(Colors.Transparent),
                        BorderThickness = new Thickness(0),
                        UseSystemFocusVisuals = false,
                        Content = new TextBlock { Text = starred ? "★" : "☆", FontSize = 12, Foreground = ThemeHelper.Brush(starred ? tint.Accent : p.T3) },
                    };
                    ToolTipService.SetToolTip(starBtn, starred ? "取消收藏模型" : "收藏常用模型");
                    AutomationProperties.SetName(starBtn, L10n.T(starred ? "取消收藏模型" : "收藏常用模型"));
                    starBtn.Click += (_, _) =>
                    {
                        _library.Companion.ToggleStarModel(preset.Id, nextM);
                        PopulateModels(filterBox.Text);
                    };
                    Grid.SetColumn(starBtn, 1);
                    rowGrid.Children.Add(starBtn);

                    return rowGrid;
                }

                if (string.IsNullOrEmpty(fq) && selectedMaker == "全部")
                {
                    var starredModels = pool.Where(m => _library.Companion.IsModelStarred(preset.Id, m)).Take(6).ToArray();
                    if (starredModels.Length > 0)
                    {
                        mStack.Children.Add(new TextBlock { Text = "已收藏", FontSize = 10.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T3), Margin = new Thickness(4, 2, 0, 2) });
                        foreach (var sm in starredModels) mStack.Children.Add(BuildModelRow(sm));
                    }
                    if (_library.Prefs.AiRecentModels.TryGetValue(preset.Id, out var recentList) && recentList.Count > 0)
                    {
                        mStack.Children.Add(new TextBlock { Text = "最近用过", FontSize = 10.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T3), Margin = new Thickness(4, 4, 0, 2) });
                        foreach (var rm in recentList.Take(3)) mStack.Children.Add(BuildModelRow(rm));
                    }
                    mStack.Children.Add(new TextBlock { Text = $"全部模型 ({pool.Count})", FontSize = 10.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T3), Margin = new Thickness(4, 6, 0, 2) });
                }

                IEnumerable<string> seq = pool;
                if (selectedMaker == "★ 收藏")
                    seq = seq.Where(m => _library.Companion.IsModelStarred(preset.Id, m));
                else if (selectedMaker != "全部")
                    seq = seq.Where(m => CompanionClient.InferModelMaker(m) == selectedMaker);

                if (!string.IsNullOrEmpty(fq))
                    seq = seq.Where(x => x.Contains(fq, StringComparison.OrdinalIgnoreCase) ||
                                         FormatFriendlyModelName(x).Contains(fq, StringComparison.OrdinalIgnoreCase) ||
                                         CompanionClient.InferModelMaker(x).Contains(fq, StringComparison.OrdinalIgnoreCase));

                var shown = seq.Take(60).ToArray();
                foreach (var mid in shown)
                    mStack.Children.Add(BuildModelRow(mid));

                if (!string.IsNullOrEmpty(fq) && !shown.Contains(fq, StringComparer.OrdinalIgnoreCase))
                {
                    var useCustomBtn = new Button
                    {
                        Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Left,
                        FontSize = 11.5,
                        Foreground = ThemeHelper.Brush(tint.Accent),
                        Content = $"在 {ShortProviderTitle(preset.Id, preset.Name)} 使用 ID：{fq}",
                        Margin = new Thickness(0, 4, 0, 0),
                    };
                    string customId = fq;
                    useCustomBtn.Click += (_, _) => SelectModelAndClose(customId);
                    mStack.Children.Add(useCustomBtn);
                }
            }

            filterBox.TextChanged += (_, _) => PopulateModels(filterBox.Text);
            PopulateModels("");

            fetchBtn.Click += async (_, _) =>
            {
                fetchBtn.IsEnabled = false;
                fetchBtn.Content = L10n.T("拉取中…");
                try
                {
                    var found = await _library.Companion.ListModelsAsync(preset.Id);
                    if (_closed) return;
                    if (found.Count > 0) _cachedCompanionModels[preset.Id] = found;
                    PopulateModels(filterBox.Text);
                }
                catch { }
                finally
                {
                    if (!_closed)
                    {
                        fetchBtn.IsEnabled = true;
                        fetchBtn.Content = L10n.T("↻ 拉取列表");
                    }
                }
            };

            if (!_cachedCompanionModels.ContainsKey(preset.Id))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var found = await _library.Companion.ListModelsAsync(preset.Id);
                        if (found.Count > 0)
                        {
                            DispatcherQueue.TryEnqueue(() =>
                            {
                                if (_closed) return;
                                _cachedCompanionModels[preset.Id] = found;
                                if (_companionPickingModel && _focusedCompanionId == preset.Id)
                                    PopulateModels(filterBox.Text);
                            });
                        }
                    }
                    catch { }
                });
            }

            InspectorHost.Children.Add(filterBox);
            InspectorHost.Children.Add(makerScroll);
            InspectorHost.Children.Add(new ScrollViewer { MaxHeight = 290, Content = mStack });

            var customBox = new TextBox { Header = "或输入自定义模型 ID", Text = curModel, FontFamily = monoFamily, FontSize = 11.5 };
            customBox.TextChanged += (_, _) =>
            {
                string nextM = customBox.Text.Trim();
                if (string.IsNullOrWhiteSpace(nextM))
                {
                    _library.Prefs.AiProviderModels.Remove(preset.Id);
                    if (inUse) _library.Prefs.AiModel = preset.DefaultModel;
                }
                else
                {
                    _library.Prefs.AiProviderModels[preset.Id] = nextM;
                }
                if (inUse) ActivateCompanionProvider(preset.Id);
                else _library.SavePrefs();
                ScheduleCompanionPageRefresh();
            };
            customBox.LostFocus += (_, _) => FlushCompanionPageRefresh();
            InspectorHost.Children.Add(customBox);
            return;
        }

        // ServicePanel 主视图（1:1 MacCompanionSettings.swift:546-732）
        var headRow = new Grid { ColumnSpacing = 10 };
        headRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        headRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headRow.Children.Add(MakeServiceLogoTile(preset.Id, 30));
        var hMeta = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        hMeta.Children.Add(new TextBlock { Text = ShortProviderTitle(preset.Id, preset.Name), FontSize = 13.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
        var hStatusTb = new TextBlock { Text = inUse ? "当前使用" : "已添加 · 未设为当前", FontSize = 11, Foreground = ThemeHelper.Brush(inUse ? p.Ok : p.T3) };
        hMeta.Children.Add(hStatusTb);
        Grid.SetColumn(hMeta, 1);
        headRow.Children.Add(hMeta);
        InspectorHost.Children.Add(headRow);

        string curFriendlyModel = string.IsNullOrEmpty(curModel) ? L10n.T("未选择") : FormatFriendlyModelName(curModel);
        var pickModelBtn = new Button
        {
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 2, 0, 2),
            FontSize = 11.5,
            Foreground = ThemeHelper.Brush(tint.Accent),
            Content = $"{curFriendlyModel} ›",
        };
        if (!string.IsNullOrEmpty(curModel))
            ToolTipService.SetToolTip(pickModelBtn, curModel);
        pickModelBtn.Click += (_, _) => { _companionPickingModel = true; RenderInspector("companion"); };

        ToggleSwitch useSw = null!;
        useSw = MakeSwitch(inUse, on =>
        {
            if (inUse)
            {
                if (!on) useSw.IsOn = true;
                return;
            }
            if (!on) return;
            string prevProv = _library.Prefs.AiProvider;
            string prevTitle = string.IsNullOrEmpty(prevProv) ? L10n.T("未设置") : ShortProviderTitle(prevProv, prevProv);
            string nextTitle = ShortProviderTitle(preset.Id, preset.Name);
            ActivateCompanionProvider(preset.Id);
            RecordPrefChange(
                nameof(DevicePrefs.AiProvider),
                "书伴服务",
                nextTitle,
                prevTitle,
                prevProv,
                preset.Id,
                undo: () =>
                {
                    if (!string.IsNullOrEmpty(prevProv)) ActivateCompanionProvider(prevProv);
                    if (_activePage == "companion") { RenderDynamicPage("companion"); RenderInspector("companion"); }
                },
                redo: () =>
                {
                    ActivateCompanionProvider(preset.Id);
                    if (_activePage == "companion") { RenderDynamicPage("companion"); RenderInspector("companion"); }
                });
            RenderDynamicPage("companion");
            RenderInspector("companion");
        });

        InspectorHost.Children.Add(MakeCard(
            MakeRow("模型", null, pickModelBtn, compact: true),
            MakeRow("设为当前服务", null, useSw, compact: true)));

        var credStack = new StackPanel { Spacing = 8 };
        if (preset.Id.StartsWith("custom.", StringComparison.OrdinalIgnoreCase))
        {
            var nameBox = new TextBox
            {
                Header = "服务名称",
                Text = ShortProviderTitle(preset.Id, preset.Name),
                PlaceholderText = "例如：公司网关",
                FontSize = 12,
            };
            nameBox.TextChanged += (_, _) =>
            {
                _library.Prefs.AiCustomProviderNames[preset.Id] = nameBox.Text.Trim();
                _library.SavePrefs();
                ScheduleCompanionPageRefresh();
            };
            nameBox.LostFocus += (_, _) => FlushCompanionPageRefresh();
            credStack.Children.Add(nameBox);
        }

        string savedKey = _library.Secrets.Get($"ai.key.{preset.Id}");
        PasswordBox? activeKeyBox = null;

        // 1:1 OpenRouterSignIn.swift：一键浏览器 PKCE 授权获取专属 Key（支持随时取消）
        if (preset.Id == "openrouter")
        {
            bool orSigningIn = _openRouterAuthCts is { IsCancellationRequested: false };
            string defaultOrBtnTitle = string.IsNullOrEmpty(savedKey) ? "用 OpenRouter 登录…" : "重新用 OpenRouter 登录…";
            var orStatus = new TextBlock
            {
                Text = orSigningIn ? L10n.T("已在浏览器打开授权页，登录完成后自动回到语阅…") : "",
                FontSize = 11,
                Foreground = ThemeHelper.Brush(p.T3),
                TextWrapping = TextWrapping.Wrap,
                Visibility = orSigningIn ? Visibility.Visible : Visibility.Collapsed,
            };
            var orRow = new Grid { ColumnSpacing = 8 };
            orRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            orRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var orBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYPrimaryPillButtonStyle"],
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Content = orSigningIn ? "等待浏览器授权…" : defaultOrBtnTitle,
                IsEnabled = !orSigningIn,
            };
            var orCancelBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
                Content = "取消",
                Visibility = orSigningIn ? Visibility.Visible : Visibility.Collapsed,
            };
            orCancelBtn.Click += (_, _) =>
            {
                try { _openRouterAuthCts?.Cancel(); _openRouterAuthCts?.Dispose(); } catch { }
                _openRouterAuthCts = null;
                orBtn.IsEnabled = true;
                orBtn.Content = L10n.T(defaultOrBtnTitle);
                orCancelBtn.Visibility = Visibility.Collapsed;
                orStatus.Text = L10n.T("已取消授权");
                orStatus.Foreground = ThemeHelper.Brush(p.T3);
                orStatus.Visibility = Visibility.Visible;
            };
            orBtn.Click += async (_, _) =>
            {
                try { _openRouterAuthCts?.Cancel(); _openRouterAuthCts?.Dispose(); } catch { }
                var cts = new CancellationTokenSource();
                _openRouterAuthCts = cts;

                orBtn.IsEnabled = false;
                orBtn.Content = L10n.T("等待浏览器授权…");
                orCancelBtn.Visibility = Visibility.Visible;
                orStatus.Text = L10n.T("已在浏览器打开授权页，登录完成后自动回到语阅…");
                orStatus.Foreground = ThemeHelper.Brush(p.T3);
                orStatus.Visibility = Visibility.Visible;
                try
                {
                    string gotKey = await CompanionClient.SignInWithOpenRouterAsync(cts.Token);
                    if (_closed) return;
                    _library.Secrets.Set("ai.key.openrouter", gotKey);
                    _newlyAddedCompanionIds.Remove("openrouter");
                    _companionEditingKey = false;
                    ActivateCompanionProvider("openrouter");
                    RecordChange("OpenRouter", "已登录");
                    if (_activePage == "companion")
                    {
                        RenderDynamicPage("companion");
                        RenderInspector("companion");
                    }
                }
                catch (OperationCanceledException)
                {
                    if (!_closed)
                    {
                        orStatus.Text = L10n.T("已取消授权");
                        orStatus.Foreground = ThemeHelper.Brush(p.T3);
                        orBtn.IsEnabled = true;
                        orBtn.Content = L10n.T(defaultOrBtnTitle);
                        orCancelBtn.Visibility = Visibility.Collapsed;
                    }
                }
                catch (Exception ex)
                {
                    if (!_closed)
                    {
                        orStatus.Text = ex.Message;
                        orStatus.Foreground = ThemeHelper.Brush(p.Danger);
                        orBtn.IsEnabled = true;
                        orBtn.Content = L10n.T(defaultOrBtnTitle);
                        orCancelBtn.Visibility = Visibility.Collapsed;
                    }
                }
                finally
                {
                    if (ReferenceEquals(_openRouterAuthCts, cts))
                    {
                        try { cts.Dispose(); } catch { }
                        _openRouterAuthCts = null;
                    }
                }
            };
            orRow.Children.Add(orBtn);
            Grid.SetColumn(orCancelBtn, 1);
            orRow.Children.Add(orCancelBtn);
            credStack.Children.Add(orRow);
            credStack.Children.Add(orStatus);
        }

        // Masked API Key row (sk-••••1234 + 更换 ›) when key is already saved and not editing
        if (preset.Group != "local" || !string.IsNullOrEmpty(savedKey) || _companionEditingKey)
        {
            if (!string.IsNullOrEmpty(savedKey) && !_companionEditingKey)
            {
                var maskedRow = new Grid { ColumnSpacing = 8, Padding = new Thickness(0, 2, 0, 2) };
                maskedRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                maskedRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                maskedRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                maskedRow.Children.Add(new TextBlock
                {
                    Text = "API Key",
                    FontSize = 12,
                    Foreground = ThemeHelper.Brush(p.T2),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                var maskedVal = new TextBlock
                {
                    Text = MaskSecretKey(savedKey),
                    FontFamily = monoFamily,
                    FontSize = 11.5,
                    Foreground = ThemeHelper.Brush(p.T3),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(maskedVal, 1);
                maskedRow.Children.Add(maskedVal);

                var editKeyBtn = new Button
                {
                    Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
                    Content = "更换 ›",
                    FontSize = 11.5,
                    Foreground = ThemeHelper.Brush(tint.Accent),
                    Padding = new Thickness(4, 2, 2, 2),
                };
                editKeyBtn.Click += (_, _) =>
                {
                    _companionEditingKey = true;
                    RenderInspector("companion");
                };
                Grid.SetColumn(editKeyBtn, 2);
                maskedRow.Children.Add(editKeyBtn);
                credStack.Children.Add(maskedRow);
            }
            else
            {
                activeKeyBox = new PasswordBox
                {
                    Header = "API Key（本机加密）",
                    Password = savedKey,
                    PlaceholderText = preset.KeyHint,
                    FontSize = 12,
                };
                activeKeyBox.PasswordChanged += (_, _) =>
                {
                    string nextK = activeKeyBox.Password.Trim();
                    _library.Secrets.Set($"ai.key.{preset.Id}", nextK);
                    if (inUse) _library.Secrets.Set("ai.apiKey", nextK);
                    ScheduleCompanionPageRefresh();
                };
                activeKeyBox.LostFocus += (_, _) => FlushCompanionPageRefresh();
                credStack.Children.Add(activeKeyBox);

                var keySubBar = new Grid();
                if (!string.IsNullOrEmpty(preset.KeyUrl))
                {
                    string kurl = preset.KeyUrl;
                    keySubBar.Children.Add(new HyperlinkButton
                    {
                        Content = $"去 {preset.Name} 控制台获取 API Key ↗",
                        FontSize = 11,
                        Padding = new Thickness(0, 2, 0, 2),
                        NavigateUri = new Uri(kurl),
                    });
                }
                if (!string.IsNullOrEmpty(savedKey))
                {
                    var doneKeyBtn = new Button
                    {
                        Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
                        Content = "收起",
                        FontSize = 11,
                        Foreground = ThemeHelper.Brush(p.T2),
                        HorizontalAlignment = HorizontalAlignment.Right,
                    };
                    doneKeyBtn.Click += (_, _) =>
                    {
                        _companionEditingKey = false;
                        RenderInspector("companion");
                    };
                    keySubBar.Children.Add(doneKeyBtn);
                }
                if (keySubBar.Children.Count > 0)
                    credStack.Children.Add(keySubBar);
            }
        }

        var urlBox = new TextBox
        {
            Header = "接口地址",
            Text = GetProviderUrl(preset.Id, preset.BaseUrl),
            PlaceholderText = string.IsNullOrEmpty(preset.BaseUrl) ? "https://.../v1" : preset.BaseUrl,
            FontFamily = monoFamily,
            FontSize = 11.5,
        };
        urlBox.TextChanged += (_, _) =>
        {
            string nextU = urlBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(nextU))
            {
                _library.Prefs.AiProviderUrls.Remove(preset.Id);
                if (inUse) _library.Prefs.AiBaseUrl = preset.BaseUrl;
            }
            else
            {
                _library.Prefs.AiProviderUrls[preset.Id] = nextU;
            }
            if (inUse) ActivateCompanionProvider(preset.Id);
            else _library.SavePrefs();
        };
        credStack.Children.Add(urlBox);

        InspectorHost.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(11),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12),
            Child = credStack,
        });

        var testResult = new TextBlock { FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var checkBtn = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            CornerRadius = new CornerRadius(10),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 8, 12, 8),
            Foreground = ThemeHelper.Brush(p.Ok),
            FontSize = 12.5,
            FontWeight = Microsoft.UI.Text.FontWeights.Medium,
            UseSystemFocusVisuals = false,
            Content = "检查连接",
        };
        checkBtn.Click += async (_, _) =>
        {
            checkBtn.IsEnabled = false;
            testResult.Text = L10n.T("正在验证连接…");
            testResult.Foreground = ThemeHelper.Brush(p.T3);
            testResult.Visibility = Visibility.Visible;
            var sw = Stopwatch.StartNew();
            string rawUrlInput = urlBox.Text.Trim();
            string testBase = string.IsNullOrEmpty(rawUrlInput) ? preset.BaseUrl : rawUrlInput;
            string testModel = GetProviderModel(preset.Id, preset.DefaultModel);
            string trimmedKey = activeKeyBox is not null ? activeKeyBox.Password.Trim() : _library.Secrets.Get($"ai.key.{preset.Id}");
            if (!string.IsNullOrEmpty(trimmedKey))
                _library.Secrets.Set($"ai.key.{preset.Id}", trimmedKey);
            if (string.IsNullOrEmpty(rawUrlInput))
            {
                _library.Prefs.AiProviderUrls.Remove(preset.Id);
                if (inUse) _library.Prefs.AiBaseUrl = preset.BaseUrl;
            }
            else
            {
                _library.Prefs.AiProviderUrls[preset.Id] = rawUrlInput;
            }

            var (ok, msg) = await _library.Companion.TestProviderAsync(preset.Id, testBase, testModel, trimmedKey);
            if (_closed) return;
            sw.Stop();
            bool autoActivated = false;
            if (ok && !inUse &&
                (string.IsNullOrWhiteSpace(_library.Prefs.AiProvider) ||
                 !_library.Companion.IsConfigured ||
                 _newlyAddedCompanionIds.Remove(preset.Id)))
            {
                ActivateCompanionProvider(preset.Id);
                inUse = true;
                useSw.IsOn = true;
                hStatusTb.Text = L10n.T("当前使用");
                hStatusTb.Foreground = ThemeHelper.Brush(p.Ok);
                autoActivated = true;
                RecordChange("书伴服务", ShortProviderTitle(preset.Id, preset.Name));
            }
            else if (ok)
            {
                _newlyAddedCompanionIds.Remove(preset.Id);
                if (inUse) ActivateCompanionProvider(preset.Id);
                else _library.SavePrefs();
            }

            if (ok && _companionEditingKey)
                _companionEditingKey = false;

            testResult.Text = ok
                ? (autoActivated ? $"✓ 正常 · {sw.Elapsed.TotalSeconds:0.1} 秒 · 已设为当前服务" : $"✓ 正常 · {sw.Elapsed.TotalSeconds:0.1} 秒")
                : msg;
            testResult.Foreground = ThemeHelper.Brush(ok ? p.Ok : p.Danger);
            checkBtn.IsEnabled = true;
            RenderDynamicPage("companion");
        };
        InspectorHost.Children.Add(checkBtn);
        InspectorHost.Children.Add(testResult);

        if (_library.Prefs.ConfiguredAiProviders.Count > 1)
        {
            string dispTitle = ShortProviderTitle(preset.Id, preset.Name);
            var rmBtn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Foreground = ThemeHelper.Brush(p.Danger),
                FontSize = 11.5,
                Content = $"移除 {dispTitle}",
            };
            rmBtn.Click += async (_, _) =>
            {
                bool confirmed = await YYChrome.ShowConfirmAsync(
                    RootGrid.XamlRoot,
                    IsDark,
                    L10n.T($"移除 {dispTitle}？"),
                    L10n.T("会同时清除这台电脑上保存的 API Key，不会影响你的云端账号。"),
                    confirmText: L10n.T("移除"),
                    cancelText: L10n.T("取消"),
                    destructive: true,
                    tint: _library.Prefs.Tint);
                if (!confirmed) return;

                _library.Prefs.ConfiguredAiProviders.Remove(preset.Id);
                _library.Secrets.Set($"ai.key.{preset.Id}", "");
                _companionEditingKey = false;
                if (inUse && _library.Prefs.ConfiguredAiProviders.Count > 0)
                    ActivateCompanionProvider(_library.Prefs.ConfiguredAiProviders[0]);
                _focusedCompanionId = _library.Prefs.AiProvider;
                _library.SavePrefs();
                RecordChange("移除书伴服务", dispTitle);
                RenderDynamicPage("companion");
                RenderInspector("companion");
            };
            InspectorHost.Children.Add(rmBtn);
        }
    }

    // ---------- 6. 同步（1:1 MacSyncSettings.swift） ----------

    private void BuildSyncPage()
    {
        var p = CurrentPalette;
        var tint = CurrentTint;
        bool isOn = _library.Sync.IsEnabled;

        // 笔记同步冲突提示条（1:1 MacSyncSettings.swift ConflictsBanner）
        try
        {
            var conflicts = _library.Core.NoteConflicts();
            if (conflicts.Length > 0)
            {
                var cfGrid = new Grid { Padding = new Thickness(14, 10, 14, 10) };
                cfGrid.Children.Add(new TextBlock
                {
                    Text = $"⚠️ 有 {conflicts.Length} 条笔记在两台设备上各有不同改动",
                    FontSize = 12.5,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = ThemeHelper.Brush(p.T1),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                var resBtn = new Button
                {
                    Style = (Style)Application.Current.Resources["YYPrimaryPillButtonStyle"],
                    Content = "查看并解决…",
                    Height = 26,
                    MinHeight = 26,
                    FontSize = 11.5,
                    HorizontalAlignment = HorizontalAlignment.Right,
                };
                resBtn.Click += async (_, _) => await SnapshotAndConflictDialogs.ShowNoteConflictsSheetAsync(RootGrid.XamlRoot, _library, () =>
                {
                    App.CurrentApp.NotifyShelfRefresh();
                    RenderDynamicPage("sync");
                });
                cfGrid.Children.Add(resBtn);
                DynamicPageHost.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(12),
                    Background = ThemeHelper.Brush(tint.Soft),
                    BorderBrush = ThemeHelper.Brush(tint.Edge),
                    BorderThickness = new Thickness(1),
                    Child = cfGrid,
                });
            }
        }
        catch { }

        var statusOuter = new StackPanel { Spacing = 8, Padding = new Thickness(14, 10, 14, 10) };
        var topLine = new Grid { ColumnSpacing = 12 };
        topLine.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        topLine.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var (iconName, iconCol) = _library.Sync.State switch
        {
            SyncStateKind.Syncing => ("sync-arrows", tint.Accent),
            SyncStateKind.Failed => ("info-circle", p.Danger),
            _ when isOn => ("checkmark", p.Ok),
            _ => ("cloud-slash", p.T3),
        };
        topLine.Children.Add(new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(16),
            Background = ThemeHelper.Brush(Color.FromArgb(26, iconCol.R, iconCol.G, iconCol.B)),
            Child = YYIcons.Make(iconName, 14, ThemeHelper.Brush(iconCol)),
        });

        string statusHeadline = _library.Sync.State switch
        {
            SyncStateKind.Syncing => "正在同步…",
            SyncStateKind.Failed => "同步未成功",
            _ when !isOn => "尚未开启同步",
            _ when _library.Sync.LastSynced is null => "已连上",
            _ => "已同步",
        };
        string statusSubline = !isOn
            ? "选择下方任一方式，跨设备同步进度、笔记与设置"
            : (_library.Sync.State == SyncStateKind.Failed
                ? (_library.Sync.LastError ?? _library.Sync.StatusSummary())
                : _library.Sync.StatusSummary());

        var statusMid = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        statusMid.Children.Add(new TextBlock
        {
            Text = statusHeadline,
            FontSize = 13.5,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = ThemeHelper.Brush(_library.Sync.State == SyncStateKind.Failed ? p.Danger : p.T1),
        });
        statusMid.Children.Add(new TextBlock
        {
            Text = statusSubline,
            FontSize = 11,
            Foreground = ThemeHelper.Brush(_library.Sync.State == SyncStateKind.Failed ? p.Danger : p.T3),
            TextWrapping = TextWrapping.Wrap,
        });
        Grid.SetColumn(statusMid, 1);
        topLine.Children.Add(statusMid);

        if (isOn)
        {
            var syncNowBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
                Content = _library.Sync.State == SyncStateKind.Syncing ? "同步中…" : "立即同步",
                IsEnabled = _library.Sync.State != SyncStateKind.Syncing,
            };
            syncNowBtn.Click += async (_, _) =>
            {
                syncNowBtn.IsEnabled = false;
                syncNowBtn.Content = L10n.T("同步中…");
                await _library.Sync.SyncNowAsync();
                App.CurrentApp.NotifyShelfRefresh();
                RenderDynamicPage("sync");
            };
            Grid.SetColumn(syncNowBtn, 2);
            topLine.Children.Add(syncNowBtn);
        }
        statusOuter.Children.Add(topLine);

        var devList = _library.GetDevices();
        if (devList.Count > 0)
        {
            var devGrid = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
            for (int c = 0; c < 3; c++) devGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            int devRows = (devList.Count + 2) / 3;
            for (int r = 0; r < devRows; r++) devGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            for (int i = 0; i < devList.Count; i++)
            {
                var d = devList[i];
                var dGrid = new Grid { ColumnSpacing = 8 };
                dGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
                dGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                dGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                string platLow = (d.Platform ?? "").ToLowerInvariant();
                string devIcon = platLow switch
                {
                    "ipados" or "tablet" => "tablet",
                    "ios" => (d.Name?.Contains("iPad", StringComparison.OrdinalIgnoreCase) == true) ? "tablet" : "phone",
                    "android" => (d.Name?.Contains("Pad", StringComparison.OrdinalIgnoreCase) == true || d.Name?.Contains("Tab", StringComparison.OrdinalIgnoreCase) == true) ? "tablet" : "phone",
                    _ => "laptop",
                };
                dGrid.Children.Add(YYIcons.Make(devIcon, 14, ThemeHelper.Brush(p.T2)));

                var dText = new StackPanel { Spacing = 1 };
                dText.Children.Add(new TextBlock { Text = string.IsNullOrEmpty(d.Name) ? Environment.MachineName : d.Name, FontSize = 11.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
                string platLabel = platLow switch { "ios" => "iPhone / iPad", "ipados" => "iPad", "android" => "Android", "macos" => "Mac", _ => "Windows" };
                string whenStr = d.ThisDevice ? "刚刚" : platLabel;
                if (!d.ThisDevice && d.LastAt is long lat && lat > 0)
                {
                    var ago = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(lat);
                    whenStr = ago.TotalMinutes < 2 ? "刚刚"
                        : ago.TotalHours < 1 ? $"{(int)ago.TotalMinutes} 分钟前"
                        : ago.TotalDays < 1 ? $"{(int)ago.TotalHours} 小时前"
                        : $"{(int)ago.TotalDays} 天前";
                }
                string? resolvedBook = null;
                if (!string.IsNullOrWhiteSpace(d.LastBook))
                {
                    var matchBook = _library.GetKeptBooks().FirstOrDefault(b => b.Id == d.LastBook);
                    if (matchBook is not null) resolvedBook = matchBook.Title;
                    else if (d.LastBook.Length < 24 || !d.LastBook.All(c => char.IsAsciiHexDigit(c) || c == '-')) resolvedBook = d.LastBook;
                }
                string bookLine = !string.IsNullOrWhiteSpace(resolvedBook)
                    ? $"{whenStr} · 《{resolvedBook}》{(d.LastFraction is double lf ? $" {Math.Round(lf * 100)}%" : "")}"
                    : whenStr;
                dText.Children.Add(new TextBlock { Text = bookLine, FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
                Grid.SetColumn(dText, 1);
                dGrid.Children.Add(dText);

                if (d.ThisDevice)
                {
                    var thisTb = new TextBlock { Text = "这台", FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T2), VerticalAlignment = VerticalAlignment.Center };
                    Grid.SetColumn(thisTb, 2);
                    dGrid.Children.Add(thisTb);
                }

                var pill = new Border { CornerRadius = new CornerRadius(10), Background = ThemeHelper.Brush(p.Inset), Padding = new Thickness(10, 6, 10, 6), Child = dGrid };
                Grid.SetRow(pill, i / 3);
                Grid.SetColumn(pill, i % 3);
                devGrid.Children.Add(pill);
            }
            statusOuter.Children.Add(devGrid);
        }
        DynamicPageHost.Children.Add(MakeCard(statusOuter));

        // 2. 同步方式（激活卡片支持右键「停止同步」，1:1 MacSyncSettings.swift:257-259）
        var waysHeading = MakeHeading("同步方式");
        waysHeading.Margin = new Thickness(14, 2, 0, -6);
        DynamicPageHost.Children.Add(waysHeading);
        var waysGrid = new Grid { ColumnSpacing = 12 };
        for (int c = 0; c < 3; c++) waysGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        UIElement BuildMethodTile(int col, string methodKey, string icon, string title, string detail, bool disabled = false)
        {
            bool on = !disabled && _library.Prefs.SyncMethod == methodKey;
            var stack = new StackPanel { Spacing = 1.5 };
            var iconLine = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            iconLine.Children.Add(new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(7.5),
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = ThemeHelper.Brush(on ? tint.Soft : p.Inset),
                Child = YYIcons.Make(icon, 13, ThemeHelper.Brush(on ? tint.Accent : p.T1)),
            });
            if (on)
            {
                var chk = YYIcons.Make("checkmark", 13, ThemeHelper.Brush(tint.Accent));
                chk.HorizontalAlignment = HorizontalAlignment.Right;
                iconLine.Children.Add(chk);
            }
            stack.Children.Add(iconLine);
            stack.Children.Add(new TextBlock { Text = title, FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
            stack.Children.Add(new TextBlock { Text = detail, FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });

            var border = new Border
            {
                CornerRadius = new CornerRadius(12),
                Background = ThemeHelper.Brush(p.Card),
                BorderBrush = ThemeHelper.Brush(on ? tint.Accent : p.Line),
                BorderThickness = new Thickness(on ? 1.5 : 1),
                Padding = new Thickness(13, 9, 13, 9),
                Opacity = disabled ? 0.55 : 1.0,
                Child = stack,
            };

            if (disabled) { Grid.SetColumn(border, col); return border; }

            MenuFlyout? ctx = null;
            if (on)
            {
                ctx = new MenuFlyout();
                var stopMi = new MenuFlyoutItem { Text = L10n.T("停止同步"), Foreground = ThemeHelper.Brush(p.Danger) };
                stopMi.Click += (_, _) =>
                {
                    _library.Sync.StopSync();
                    RecordChange("同步", "已停止");
                    App.CurrentApp.NotifyShelfRefresh();
                    RenderDynamicPage("sync");
                };
                ctx.Items.Add(stopMi);
            }

            var btn = new Button
            {
                Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(12),
                UseSystemFocusVisuals = false,
                Content = border,
                ContextFlyout = ctx,
            };
            btn.Click += async (_, _) =>
            {
                if (methodKey == "folder")
                {
                    _editingWebDav = false;
                    _editingGDrive = false;
                    string? folderPath = await NativePickerHelper.PickSingleFolderAsync(this);
                    if (!string.IsNullOrEmpty(folderPath))
                    {
                        _library.Prefs.SyncMethod = "folder";
                        _library.Prefs.SyncLocalFolder = folderPath;
                        _library.SavePrefs();
                        RecordChange("同步", "个人云盘");
                        await _library.Sync.SyncNowAsync();
                        App.CurrentApp.NotifyShelfRefresh();
                        RenderDynamicPage("sync");
                    }
                }
                else if (methodKey == "webdav")
                {
                    _editingGDrive = false;
                    _editingWebDav = !_editingWebDav;
                    RenderDynamicPage("sync");
                }
                else if (methodKey == "gdrive")
                {
                    _editingWebDav = false;
                    _editingGDrive = !_editingGDrive;
                    RenderDynamicPage("sync");
                }
            };
            Grid.SetColumn(btn, col);
            return btn;
        }

        string gdriveSub = _library.Prefs.SyncMethod == "gdrive" && !string.IsNullOrWhiteSpace(_library.Prefs.GoogleEmail)
            ? _library.Prefs.GoogleEmail
            : "隐藏应用目录 · 跨 Android / iOS 汇合";
        waysGrid.Children.Add(BuildMethodTile(0, "folder", "cloud", "个人云盘", "OneDrive / iCloud 云盘同步文件夹"));
        waysGrid.Children.Add(BuildMethodTile(1, "webdav", "server", "WebDAV", "坚果云、群晖、Nextcloud…"));
        waysGrid.Children.Add(BuildMethodTile(2, "gdrive", "globe", "Google Drive", gdriveSub));
        DynamicPageHost.Children.Add(waysGrid);

        if (_editingGDrive)
        {
            bool gdriveWaiting = _gdriveAuthCts is { IsCancellationRequested: false };
            string defaultGLoginLabel = _library.Prefs.SyncMethod == "gdrive" ? "重新登录 Google 账号" : "用 Google 账号登录并开启同步";
            var gStatus = new TextBlock
            {
                Text = gdriveWaiting
                    ? L10n.T("已在浏览器打开 Google 授权页，完成授权后自动开启同步…")
                    : "数据加密存放在你的 Google Drive 应用专属目录，语阅无法看到云盘里的其他文件。",
                FontSize = 11.5,
                Foreground = ThemeHelper.Brush(p.T3),
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
            };
            var gCancelBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
                Content = "取消",
                Visibility = gdriveWaiting ? Visibility.Visible : Visibility.Collapsed,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var gLoginBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYPrimaryPillButtonStyle"],
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Content = gdriveWaiting ? "等待授权…" : defaultGLoginLabel,
                IsEnabled = !gdriveWaiting,
            };
            gCancelBtn.Click += (_, _) =>
            {
                try { _gdriveAuthCts?.Cancel(); _gdriveAuthCts?.Dispose(); } catch { }
                _gdriveAuthCts = null;
                gCancelBtn.Visibility = Visibility.Collapsed;
                gLoginBtn.IsEnabled = true;
                gLoginBtn.Content = L10n.T(defaultGLoginLabel);
                gStatus.Text = L10n.T("已取消 Google 授权");
                gStatus.Foreground = ThemeHelper.Brush(p.T3);
            };
            gLoginBtn.Click += async (_, _) =>
            {
                try { _gdriveAuthCts?.Cancel(); _gdriveAuthCts?.Dispose(); } catch { }
                var cts = new CancellationTokenSource();
                _gdriveAuthCts = cts;

                gLoginBtn.IsEnabled = false;
                gLoginBtn.Content = L10n.T("等待授权…");
                gCancelBtn.Visibility = Visibility.Visible;
                gStatus.Text = L10n.T("已在浏览器打开 Google 授权页，完成授权后自动开启同步…");
                gStatus.Foreground = ThemeHelper.Brush(p.T3);
                try
                {
                    await _library.Sync.ConnectGoogleDriveAsync(cts.Token);
                    if (_closed) return;
                    _editingGDrive = false;
                    RecordChange("同步", "Google Drive");
                    App.CurrentApp.NotifyShelfRefresh();
                    if (_activePage == "sync") RenderDynamicPage("sync");
                }
                catch (OperationCanceledException)
                {
                    if (!_closed)
                    {
                        gStatus.Text = L10n.T("已取消 Google 授权");
                        gStatus.Foreground = ThemeHelper.Brush(p.T3);
                        gLoginBtn.IsEnabled = true;
                        gLoginBtn.Content = L10n.T(defaultGLoginLabel);
                        gCancelBtn.Visibility = Visibility.Collapsed;
                    }
                }
                catch (Exception ex)
                {
                    if (!_closed)
                    {
                        gStatus.Text = ex.Message;
                        gStatus.Foreground = ThemeHelper.Brush(p.Danger);
                        gLoginBtn.IsEnabled = true;
                        gLoginBtn.Content = L10n.T(defaultGLoginLabel);
                        gCancelBtn.Visibility = Visibility.Collapsed;
                    }
                }
                finally
                {
                    if (ReferenceEquals(_gdriveAuthCts, cts))
                    {
                        try { cts.Dispose(); } catch { }
                        _gdriveAuthCts = null;
                    }
                }
            };

            var gBtnGroup = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            gBtnGroup.Children.Add(gCancelBtn);
            gBtnGroup.Children.Add(gLoginBtn);

            var gFoot = new Grid { Padding = new Thickness(16, 12, 16, 12), ColumnSpacing = 12 };
            gFoot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            gFoot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            gFoot.Children.Add(gStatus);
            Grid.SetColumn(gBtnGroup, 1);
            gFoot.Children.Add(gBtnGroup);

            if (string.IsNullOrWhiteSpace(_library.Sync.EffectiveGoogleClientId))
            {
                DynamicPageHost.Children.Add(MakeCard(
                    MakeInlineInputRow("Client ID", _library.Sync.EffectiveGoogleClientId, "桌面端 OAuth Client ID（xxxx.apps.googleusercontent.com）", v => { _library.Prefs.GoogleClientId = v.Trim(); _library.SavePrefs(); }),
                    gFoot));
            }
            else
            {
                DynamicPageHost.Children.Add(MakeCard(gFoot));
            }
        }
        else if (_editingWebDav)
        {
            var checkNote = new TextBlock { FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            var checkCardHost = new ContentControl { Margin = new Thickness(0, 6, 0, 0) };
            CancellationTokenSource? checkCts = null;

            var cancelCheckBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
                Content = "取消",
                Visibility = Visibility.Collapsed,
            };
            var connectBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYPrimaryPillButtonStyle"],
                Content = _library.Prefs.SyncMethod == "webdav" ? "重新检查并连接" : "检查连接并开启同步",
            };

            cancelCheckBtn.Click += (_, _) => checkCts?.Cancel();
            connectBtn.Click += async (_, _) =>
            {
                checkCts?.Cancel();
                checkCts?.Dispose();
                var cts = new CancellationTokenSource();
                checkCts = cts;

                connectBtn.IsEnabled = false;
                cancelCheckBtn.Visibility = Visibility.Visible;
                checkNote.Text = L10n.T("正在依次检查服务器、TLS 证书、账号密码与读写权限…");
                checkNote.Foreground = ThemeHelper.Brush(p.T3);

                try
                {
                    var report = await WebDavCheckService.RunAsync(
                        _library.Prefs.WebDavUrl,
                        _library.Prefs.WebDavUser,
                        _library.Secrets.Get("sync.webdav.password"),
                        _library.Prefs.WebDavFolder,
                        ct: cts.Token,
                        onProgress: steps => DispatcherQueue.TryEnqueue(() =>
                        {
                            if (_closed) return;
                            if (_activePage == "sync" && _editingWebDav)
                                checkCardHost.Content = WebDavCheckService.BuildCheckCard(steps, p, tint, isRunning: true);
                        }));

                    if (_closed) return;
                    checkCardHost.Content = WebDavCheckService.BuildCheckCard(report, p, tint);
                    checkNote.Text = report.AllPassed ? $"✓ 四项检查全部通过（{report.ElapsedSeconds:0.1} 秒）" : L10n.T("请根据下方检查结果修正后重试");
                    checkNote.Foreground = ThemeHelper.Brush(report.AllPassed ? p.Ok : p.Danger);
                    if (report.AllPassed)
                    {
                        _library.Prefs.SyncMethod = "webdav";
                        _library.SavePrefs();
                        RecordChange("同步", "WebDAV");
                        await _library.Sync.SyncNowAsync();
                        if (_closed) return;
                        _editingWebDav = false;
                        App.CurrentApp.NotifyShelfRefresh();
                        if (_activePage == "sync") RenderDynamicPage("sync");
                    }
                }
                catch (OperationCanceledException)
                {
                    if (!_closed)
                    {
                        checkNote.Text = L10n.T("已取消检查");
                        checkNote.Foreground = ThemeHelper.Brush(p.T3);
                    }
                }
                finally
                {
                    if (!_closed)
                    {
                        cancelCheckBtn.Visibility = Visibility.Collapsed;
                        connectBtn.IsEnabled = true;
                    }
                }
            };

            var footBar = new Grid { Padding = new Thickness(16, 10, 16, 10), ColumnSpacing = 12 };
            footBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            footBar.Children.Add(checkNote);
            var btnGroup = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
            btnGroup.Children.Add(cancelCheckBtn);
            btnGroup.Children.Add(connectBtn);
            Grid.SetColumn(btnGroup, 1);
            footBar.Children.Add(btnGroup);

            DynamicPageHost.Children.Add(MakeCard(
                MakeInlineInputRow("服务器地址", _library.Prefs.WebDavUrl, "https://dav.jianguoyun.com/dav/", v => { _library.Prefs.WebDavUrl = v.Trim(); _library.SavePrefs(); }),
                MakeInlineInputRow("账号", _library.Prefs.WebDavUser, "坚果云邮箱或用户名", v => { _library.Prefs.WebDavUser = v.Trim(); _library.SavePrefs(); }),
                MakeInlineInputRow("密码", _library.Secrets.Get("sync.webdav.password"), "填“应用密码”；仅加密存在本机", v => _library.Secrets.Set("sync.webdav.password", v), isPassword: true),
                MakeInlineInputRow("文件夹", _library.Prefs.WebDavFolder, "语阅", v => { _library.Prefs.WebDavFolder = v.Trim(); _library.SavePrefs(); }),
                footBar));
            DynamicPageHost.Children.Add(checkCardHost);
        }
        else if (_library.Prefs.SyncMethod == "webdav")
        {
            var editBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "修改" };
            editBtn.Click += (_, _) => { _editingWebDav = true; RenderDynamicPage("sync"); };
            DynamicPageHost.Children.Add(MakeCard(MakeRow("WebDAV 服务器", $"{_library.Prefs.WebDavUrl} · {_library.Prefs.WebDavUser}", editBtn, compact: true)));
        }
        else if (_library.Prefs.SyncMethod == "gdrive")
        {
            var gOutBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
                Content = "退出",
                Foreground = ThemeHelper.Brush(p.Danger),
            };
            gOutBtn.Click += async (_, _) =>
            {
                bool ok = await YYChrome.ShowConfirmAsync(
                    RootGrid.XamlRoot,
                    IsDark,
                    L10n.T("退出 Google Drive 同步？"),
                    L10n.T("这台电脑将停止同步，云盘上已有的数据不会删除。"),
                    confirmText: L10n.T("退出"),
                    cancelText: L10n.T("取消"),
                    destructive: true,
                    tint: _library.Prefs.Tint);
                if (!ok) return;
                _library.Sync.StopSync();
                RecordChange("Google 账号", "已退出");
                App.CurrentApp.NotifyShelfRefresh();
                RenderDynamicPage("sync");
            };
            string gAccountDetail = string.IsNullOrWhiteSpace(_library.Prefs.GoogleEmail)
                ? "存放于 Google Drive 应用专属目录"
                : $"{_library.Prefs.GoogleEmail} · 存放于 Google Drive 应用专属目录";
            DynamicPageHost.Children.Add(MakeCard(MakeRow("Google 账号", gAccountDetail, gOutBtn, compact: true)));
        }
        else if (_library.Prefs.SyncMethod == "folder" && !string.IsNullOrEmpty(_library.Prefs.SyncLocalFolder))
        {
            var folderBtns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var changeFolderBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "更换文件夹" };
            changeFolderBtn.Click += async (_, _) =>
            {
                string? pickedPath = await NativePickerHelper.PickSingleFolderAsync(this);
                if (!string.IsNullOrEmpty(pickedPath))
                {
                    _library.Prefs.SyncLocalFolder = pickedPath;
                    _library.SavePrefs();
                    await _library.Sync.SyncNowAsync();
                    App.CurrentApp.NotifyShelfRefresh();
                    RenderDynamicPage("sync");
                }
            };
            var openFolderBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "在资源管理器中查看" };
            openFolderBtn.Click += (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_library.Prefs.SyncLocalFolder}\"") { UseShellExecute = true });
            folderBtns.Children.Add(changeFolderBtn);
            folderBtns.Children.Add(openFolderBtn);
            DynamicPageHost.Children.Add(MakeCard(MakeRow("同步文件夹", _library.Prefs.SyncLocalFolder, folderBtns, compact: true)));
        }

        // 3. 同步内容（未开启同步时文本保持清晰、仅开关置灰禁用，1:1 05-sync.png）
        var contentHeading = MakeHeading("同步内容");
        contentHeading.Margin = new Thickness(14, 2, 0, -6);
        DynamicPageHost.Children.Add(contentHeading);
        string placeName = _library.Prefs.SyncMethod switch
        {
            "webdav" => "网盘",
            "gdrive" => "Google 云端硬盘",
            _ => "个人云盘",
        };
        var filesRowSub = new TextBlock
        {
            Text = $"正在计算书文件大小… · 占用你的{placeName}空间；播客单集体积很小，始终同步",
            FontSize = 10.5,
            Foreground = ThemeHelper.Brush(p.T3),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 14,
        };
        var filesRowLeft = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        filesRowLeft.Children.Add(new TextBlock { Text = "书文件", FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1) });
        filesRowLeft.Children.Add(filesRowSub);
        var filesSw = MakeSwitch(_library.Prefs.SyncFiles, v =>
        {
            bool prev = _library.Prefs.SyncFiles;
            if (prev == v) return;
            _library.Prefs.SyncFiles = v;
            _library.SavePrefs();
            RecordPrefChange(
                nameof(DevicePrefs.SyncFiles),
                "同步书文件",
                v ? "开" : "关",
                prev ? "开" : "关",
                prev,
                v,
                undo: () => { _library.Prefs.SyncFiles = prev; _library.SavePrefs(); if (_activePage == "sync") RenderDynamicPage("sync"); },
                redo: () => { _library.Prefs.SyncFiles = v; _library.SavePrefs(); if (_activePage == "sync") RenderDynamicPage("sync"); });
            if (_library.Sync.IsEnabled) _ = _library.Sync.SyncNowAsync();
        }, enabled: isOn);
        filesSw.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(filesSw, L10n.T("书文件"));
        var filesRow = new Grid { ColumnSpacing = 12, Padding = new Thickness(16, 4, 16, 4), MinHeight = 36 };
        filesRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        filesRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        filesRow.Children.Add(filesRowLeft);
        Grid.SetColumn(filesSw, 1);
        filesRow.Children.Add(filesSw);

        const string recBaseHint = "同步完整音频文件；关闭时仅同步句级对齐信息，其他设备导入同名音频即可直接跟读";
        var recRowSub = new TextBlock
        {
            Text = recBaseHint,
            FontSize = 10.5,
            Foreground = ThemeHelper.Brush(p.T3),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 14,
        };
        var recRowLeft = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        recRowLeft.Children.Add(new TextBlock { Text = "有声书录音", FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1) });
        recRowLeft.Children.Add(recRowSub);
        var recSw = MakeSwitch(_library.Prefs.SyncRecordings, v =>
        {
            bool prev = _library.Prefs.SyncRecordings;
            if (prev == v) return;
            _library.Prefs.SyncRecordings = v;
            _library.SavePrefs();
            RecordPrefChange(
                nameof(DevicePrefs.SyncRecordings),
                "同步录音",
                v ? "开" : "关",
                prev ? "开" : "关",
                prev,
                v,
                undo: () => { _library.Prefs.SyncRecordings = prev; _library.SavePrefs(); if (_activePage == "sync") RenderDynamicPage("sync"); },
                redo: () => { _library.Prefs.SyncRecordings = v; _library.SavePrefs(); if (_activePage == "sync") RenderDynamicPage("sync"); });
            if (_library.Sync.IsEnabled) _ = _library.Sync.SyncNowAsync();
        }, enabled: isOn);
        recSw.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(recSw, L10n.T("有声书录音"));
        var recRow = new Grid { ColumnSpacing = 12, Padding = new Thickness(16, 4, 16, 4), MinHeight = 36 };
        recRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        recRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        recRow.Children.Add(recRowLeft);
        Grid.SetColumn(recSw, 1);
        recRow.Children.Add(recSw);

        string dataRoot = _library.DataDir;
        _ = Task.Run(() =>
        {
            long bBytes = DirSize(Path.Combine(dataRoot, "books"));
            long rBytes = DirSize(Path.Combine(dataRoot, "audiobooks")) + DirSize(Path.Combine(dataRoot, "recordings"));
            return (bBytes, rBytes);
        }).ContinueWith(t =>
        {
            if (t.Status != TaskStatus.RanToCompletion) return;
            var (bytes, recBytes) = t.Result;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_closed || _activePage != "sync") return;
                filesRowSub.Text = L10n.T($"{FormatBytes(bytes)} · 占用你的{placeName}空间；播客单集体积很小，始终同步");
                if (recBytes > 0)
                    recRowSub.Text = $"{FormatBytes(recBytes)} · {L10n.T(recBaseHint)}";
            });
        });

        Grid MakeSyncRow(string title, string? sub, ToggleSwitch sw)
        {
            var g = MakeRow(title, sub, sw, compact: true);
            g.Padding = new Thickness(16, string.IsNullOrEmpty(sub) ? 2 : 4, 16, string.IsNullOrEmpty(sub) ? 2 : 4);
            g.MinHeight = 34;
            return g;
        }

        var syncContentCard = MakeCard(
            MakeSyncRow("进度和书签", null, MakeSwitch(_library.Prefs.SyncReading, v =>
            {
                bool prev = _library.Prefs.SyncReading;
                if (prev == v) return;
                _library.Prefs.SyncReading = v;
                _library.SavePrefs();
                RecordPrefChange(
                    nameof(DevicePrefs.SyncReading),
                    "同步进度和书签",
                    v ? "开" : "关",
                    prev ? "开" : "关",
                    prev,
                    v,
                    undo: () => { _library.Prefs.SyncReading = prev; _library.SavePrefs(); if (_activePage == "sync") RenderDynamicPage("sync"); },
                    redo: () => { _library.Prefs.SyncReading = v; _library.SavePrefs(); if (_activePage == "sync") RenderDynamicPage("sync"); });
                if (_library.Sync.IsEnabled) _ = _library.Sync.SyncNowAsync();
            }, enabled: isOn)),
            MakeSyncRow("笔记和生词", null, MakeSwitch(_library.Prefs.SyncNotes, v =>
            {
                bool prev = _library.Prefs.SyncNotes;
                if (prev == v) return;
                _library.Prefs.SyncNotes = v;
                _library.SavePrefs();
                RecordPrefChange(
                    nameof(DevicePrefs.SyncNotes),
                    "同步笔记和生词",
                    v ? "开" : "关",
                    prev ? "开" : "关",
                    prev,
                    v,
                    undo: () => { _library.Prefs.SyncNotes = prev; _library.SavePrefs(); if (_activePage == "sync") RenderDynamicPage("sync"); },
                    redo: () => { _library.Prefs.SyncNotes = v; _library.SavePrefs(); if (_activePage == "sync") RenderDynamicPage("sync"); });
                if (_library.Sync.IsEnabled) _ = _library.Sync.SyncNowAsync();
            }, enabled: isOn)),
            MakeSyncRow("设置和替换规则", null, MakeSwitch(_library.Prefs.SyncSettings, v =>
            {
                bool prev = _library.Prefs.SyncSettings;
                if (prev == v) return;
                _library.Prefs.SyncSettings = v;
                _library.SavePrefs();
                RecordPrefChange(
                    nameof(DevicePrefs.SyncSettings),
                    "同步设置",
                    v ? "开" : "关",
                    prev ? "开" : "关",
                    prev,
                    v,
                    undo: () => { _library.Prefs.SyncSettings = prev; _library.SavePrefs(); if (_activePage == "sync") RenderDynamicPage("sync"); },
                    redo: () => { _library.Prefs.SyncSettings = v; _library.SavePrefs(); if (_activePage == "sync") RenderDynamicPage("sync"); });
                if (_library.Sync.IsEnabled) _ = _library.Sync.SyncNowAsync();
            }, enabled: isOn)),
            filesRow,
            recRow);
        DynamicPageHost.Children.Add(syncContentCard);

        var lockFoot = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(14, -2, 0, 0) };
        lockFoot.Children.Add(YYIcons.Make("lock", 12, ThemeHelper.Brush(p.T3)));
        lockFoot.Children.Add(new TextBlock { Text = "无需注册账号，数据仅在你的设备与个人网盘之间传输", FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Center });
        DynamicPageHost.Children.Add(lockFoot);
    }

    // ---------- 7. 笔记导出（1:1 MacNoteExportSettings.swift：4 目标开关 + 右栏上连接/下样张） ----------

    private void BuildExportPage()
    {
        var p = CurrentPalette;
        DynamicPageHost.Children.Add(MakeHeading("导出目标"));

        var rows = new StackPanel { Spacing = 2, Padding = new Thickness(4) };

        void AddTargetRow(int idx, string key, string badge, Color badgeCol, string title, string detail, bool connected, bool isOn, Action<bool> onToggle)
        {
            if (idx > 0) rows.Children.Add(new Border { Height = 1, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(12, 0, 12, 0) });
            bool sel = _focusedExportTarget == key;

            var leftGrid = new Grid { ColumnSpacing = 12 };
            leftGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            leftGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            leftGrid.Children.Add(YYChrome.MakeServiceTile(key, IsDark, 32));

            var mid = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            mid.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1) });
            mid.Children.Add(new TextBlock { Text = detail, FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(mid, 1);
            leftGrid.Children.Add(mid);

            var selectBtn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Padding = new Thickness(12, 8, 8, 8),
                CornerRadius = new CornerRadius(9, 0, 0, 9),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                UseSystemFocusVisuals = false,
                Content = leftGrid,
            };
            AutomationProperties.SetName(selectBtn, L10n.T(title));
            selectBtn.Click += (_, _) =>
            {
                if (_focusedExportTarget == key) return;
                _focusedExportTarget = key;
                RenderDynamicPage("export");
                RenderInspector("export");
            };

            ToggleSwitch sw = null!;
            sw = MakeSwitch(connected && isOn, v =>
            {
                _focusedExportTarget = key;
                if (!connected)
                {
                    if (v) sw.IsOn = false;
                    RenderDynamicPage("export");
                    RenderInspector("export");
                    return;
                }
                onToggle(v);
                RenderDynamicPage("export");
                RenderInspector("export");
            });
            sw.VerticalAlignment = VerticalAlignment.Center;
            sw.Margin = new Thickness(0, 0, 12, 0);
            AutomationProperties.SetName(sw, L10n.T(title));

            var g = new Grid { ColumnSpacing = 8, MinHeight = 48 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(selectBtn);
            Grid.SetColumn(sw, 1);
            g.Children.Add(sw);

            var wrap = new Border
            {
                CornerRadius = new CornerRadius(9),
                Background = sel ? ThemeHelper.Brush(Color.FromArgb(16, p.T1.R, p.T1.G, p.T1.B)) : new SolidColorBrush(Colors.Transparent),
                Child = g,
            };
            rows.Children.Add(wrap);
        }

        // Migrate legacy plaintext flomo webhook URL into encrypted SecretStore once
        if (string.IsNullOrEmpty(_library.Secrets.Get("export.flomo.url")) && !string.IsNullOrEmpty(_library.Prefs.FlomoWebhookUrl))
        {
            _library.Secrets.Set("export.flomo.url", _library.Prefs.FlomoWebhookUrl.Trim());
            _library.Prefs.FlomoWebhookUrl = "";
            _library.SavePrefs();
        }

        bool obsConn = !string.IsNullOrEmpty(_library.Prefs.ObsidianFolder);
        bool notConn = !string.IsNullOrEmpty(_library.Secrets.Get("export.notion.token")) && !string.IsNullOrEmpty(_library.Prefs.NotionPageId);
        bool floConn = NoteExportService.ValidateFlomoWebhook(_library.Secrets.Get("export.flomo.url"));
        bool xinConn = !string.IsNullOrEmpty(_library.Secrets.Get("export.xinzhi.token"));

        AddTargetRow(0, "obsidian", "Ob", Color.FromArgb(255, 124, 58, 237), "Obsidian",
            obsConn ? _library.Prefs.ObsidianFolder : "未连接",
            obsConn,
            _library.Prefs.ObsidianExportOn,
            v =>
            {
                bool prev = _library.Prefs.ObsidianExportOn;
                if (prev == v) return;
                _library.Prefs.ObsidianExportOn = v;
                _library.SavePrefs();
                RecordPrefChange(nameof(DevicePrefs.ObsidianExportOn), "Obsidian 导出", v ? "开" : "关", prev ? "开" : "关", prev, v,
                    undo: () => { _library.Prefs.ObsidianExportOn = prev; _library.SavePrefs(); if (_activePage == "export") RenderDynamicPage("export"); },
                    redo: () => { _library.Prefs.ObsidianExportOn = v; _library.SavePrefs(); if (_activePage == "export") RenderDynamicPage("export"); });
            });

        AddTargetRow(1, "notion", "N", p.T1, "Notion",
            notConn ? $"放在“{(string.IsNullOrEmpty(_library.Prefs.NotionPageTitle) ? "读书笔记" : _library.Prefs.NotionPageTitle)}”下面，每本书一页" : "未连接",
            notConn,
            _library.Prefs.NotionExportOn,
            v =>
            {
                bool prev = _library.Prefs.NotionExportOn;
                if (prev == v) return;
                _library.Prefs.NotionExportOn = v;
                _library.SavePrefs();
                RecordPrefChange(nameof(DevicePrefs.NotionExportOn), "Notion 导出", v ? "开" : "关", prev ? "开" : "关", prev, v,
                    undo: () => { _library.Prefs.NotionExportOn = prev; _library.SavePrefs(); if (_activePage == "export") RenderDynamicPage("export"); },
                    redo: () => { _library.Prefs.NotionExportOn = v; _library.SavePrefs(); if (_activePage == "export") RenderDynamicPage("export"); });
            });

        AddTargetRow(2, "flomo", "fl", Color.FromArgb(255, 46, 158, 91), "flomo",
            floConn ? "每条划线一条笔记" : "未连接",
            floConn,
            _library.Prefs.FlomoExportOn,
            v =>
            {
                bool prev = _library.Prefs.FlomoExportOn;
                if (prev == v) return;
                _library.Prefs.FlomoExportOn = v;
                _library.SavePrefs();
                RecordPrefChange(nameof(DevicePrefs.FlomoExportOn), "flomo 导出", v ? "开" : "关", prev ? "开" : "关", prev, v,
                    undo: () => { _library.Prefs.FlomoExportOn = prev; _library.SavePrefs(); if (_activePage == "export") RenderDynamicPage("export"); },
                    redo: () => { _library.Prefs.FlomoExportOn = v; _library.SavePrefs(); if (_activePage == "export") RenderDynamicPage("export"); });
            });

        AddTargetRow(3, "xinzhi", "枝", Color.FromArgb(255, 59, 130, 246), "新枝",
            xinConn ? "每条划线一条笔记" : "未连接",
            xinConn,
            _library.Prefs.XinzhiExportOn,
            v =>
            {
                bool prev = _library.Prefs.XinzhiExportOn;
                if (prev == v) return;
                _library.Prefs.XinzhiExportOn = v;
                _library.SavePrefs();
                RecordPrefChange(nameof(DevicePrefs.XinzhiExportOn), "新枝导出", v ? "开" : "关", prev ? "开" : "关", prev, v,
                    undo: () => { _library.Prefs.XinzhiExportOn = prev; _library.SavePrefs(); if (_activePage == "export") RenderDynamicPage("export"); },
                    redo: () => { _library.Prefs.XinzhiExportOn = v; _library.SavePrefs(); if (_activePage == "export") RenderDynamicPage("export"); });
            });

        DynamicPageHost.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(1),
            Child = rows,
        });
        DynamicPageHost.Children.Add(MakeNote("开启的项目会显示在笔记页「导出」菜单中。令牌与目录仅保存在本机，不同步、不上传。"));
    }

    private void BuildExportInspector(YYPalette p, YYTintColors tint)
    {
        // 上半区：连接与目录设置面板（1:1 MacNoteExportSettings.swift:185-695：实时验证、立即发送、保留去重的断开）
        var setupStack = new StackPanel { Spacing = 8 };
        string initialStatus = _library.Prefs.ExportLastSummary.TryGetValue(_focusedExportTarget, out var lastSum) ? lastSum : "";
        var statusNote = new TextBlock
        {
            Text = initialStatus,
            FontSize = 11,
            Foreground = ThemeHelper.Brush(p.T3),
            TextWrapping = TextWrapping.Wrap,
            Visibility = string.IsNullOrEmpty(initialStatus) ? Visibility.Collapsed : Visibility.Visible,
        };
        void SetExportStatus(string msg, Color col)
        {
            statusNote.Text = msg;
            statusNote.Foreground = ThemeHelper.Brush(col);
            statusNote.Visibility = string.IsNullOrEmpty(msg) ? Visibility.Collapsed : Visibility.Visible;
        }

        async Task ConfirmAndDisconnectExportAsync(string key, string displayName)
        {
            bool ok = await YYChrome.ShowConfirmAsync(
                RootGrid.XamlRoot,
                IsDark,
                L10n.T($"断开 {displayName}？"),
                L10n.T("会清除这台电脑上的连接设置，已经导出的笔记不会受影响。"),
                confirmText: L10n.T("断开"),
                cancelText: L10n.T("取消"),
                destructive: true,
                tint: _library.Prefs.Tint);
            if (!ok) return;
            _library.NoteExports.DisconnectTarget(key);
            RecordChange($"{displayName} 导出", "已断开");
            RenderDynamicPage("export");
            RenderInspector("export");
        }

        switch (_focusedExportTarget)
        {
            case "notion":
            {
                bool conn = !string.IsNullOrEmpty(_library.Secrets.Get("export.notion.token")) && !string.IsNullOrEmpty(_library.Prefs.NotionPageId);
                setupStack.Children.Add(new TextBlock { Text = conn ? $"Notion · 已连至「{_library.Prefs.NotionPageTitle}」" : "连接 Notion", FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
                var tkBox = new PasswordBox { Header = "Integration Token", Password = _library.Secrets.Get("export.notion.token"), PlaceholderText = "ntn_ 或 secret_..." };
                var pgBox = new TextBox { Header = "目标父页面链接或 32 位 ID", Text = !string.IsNullOrEmpty(_library.Prefs.NotionPageId) ? _library.Prefs.NotionPageId : _library.Prefs.NotionPageTitle, PlaceholderText = "粘贴 Notion 页面链接…" };

                // Clipboard auto-detect for Notion Token or Page URL
                var clipBtn = new Button
                {
                    Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    FontSize = 11,
                    Height = 25,
                    MinHeight = 25,
                    Visibility = Visibility.Collapsed,
                };
                _ = Task.Run(async () =>
                {
                    string? clipText = null;
                    try
                    {
                        var tcs = new TaskCompletionSource<string?>();
                        DispatcherQueue.TryEnqueue(async () =>
                        {
                            try
                            {
                                if (_closed || _activePage != "export" || _focusedExportTarget != "notion")
                                {
                                    tcs.TrySetResult(null);
                                    return;
                                }
                                var pkg = Clipboard.GetContent();
                                if (pkg.Contains(StandardDataFormats.Text))
                                    tcs.TrySetResult((await pkg.GetTextAsync()).Trim());
                                else
                                    tcs.TrySetResult(null);
                            }
                            catch { tcs.TrySetResult(null); }
                        });
                        clipText = await tcs.Task;
                    }
                    catch { }
                    if (string.IsNullOrWhiteSpace(clipText)) return;
                    bool isTok = clipText.StartsWith("ntn_", StringComparison.Ordinal) || clipText.StartsWith("secret_", StringComparison.Ordinal);
                    bool isPage = clipText.Contains("notion.so/", StringComparison.OrdinalIgnoreCase) && NoteExportService.ExtractNotionPageId(clipText) is not null;
                    if (!isTok && !isPage) return;
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (_closed || _activePage != "export" || _focusedExportTarget != "notion") return;
                        clipBtn.Content = L10n.T(isTok ? "✨ 填入剪贴板中的 Notion Token" : "✨ 填入剪贴板中的 Notion 页面链接");
                        clipBtn.Visibility = Visibility.Visible;
                        clipBtn.Click += (_, _) =>
                        {
                            if (isTok) tkBox.Password = clipText;
                            else pgBox.Text = clipText;
                            clipBtn.Visibility = Visibility.Collapsed;
                        };
                    });
                });

                var verifyBtn = new Button { Style = (Style)Application.Current.Resources["YYPrimaryPillButtonStyle"], HorizontalAlignment = HorizontalAlignment.Stretch, Content = conn ? "重新验证连接" : "验证并连接 Notion" };
                verifyBtn.Click += async (_, _) =>
                {
                    verifyBtn.IsEnabled = false;
                    SetExportStatus(L10n.T("正在校验 Notion Token 与父页面权限…"), p.T3);
                    var (ok, pageTitle, msg) = await _library.NoteExports.VerifyNotionAsync(tkBox.Password, pgBox.Text);
                    if (_closed) return;
                    SetExportStatus(msg, ok ? p.Ok : p.Danger);
                    verifyBtn.IsEnabled = true;
                    if (ok)
                    {
                        _library.Secrets.Set("export.notion.token", tkBox.Password.Trim());
                        _library.Prefs.NotionPageId = NoteExportService.ExtractNotionPageId(pgBox.Text) ?? "";
                        _library.Prefs.NotionPageTitle = pageTitle;
                        _library.Prefs.NotionExportOn = true;
                        _library.SavePrefs();
                        RecordChange("Notion 导出", pageTitle);
                        RenderDynamicPage("export");
                        RenderInspector("export");
                    }
                };
                setupStack.Children.Add(clipBtn);
                setupStack.Children.Add(tkBox);
                setupStack.Children.Add(pgBox);
                setupStack.Children.Add(new HyperlinkButton
                {
                    Content = "前往 Notion Integrations 创建令牌 ↗",
                    FontSize = 11,
                    Padding = new Thickness(0, 1, 0, 1),
                    NavigateUri = new Uri("https://www.notion.so/my-integrations"),
                });
                setupStack.Children.Add(verifyBtn);
                if (conn)
                {
                    var sendBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], HorizontalAlignment = HorizontalAlignment.Stretch, Content = "立即发送全库未同步笔记" };
                    sendBtn.Click += async (_, _) =>
                    {
                        sendBtn.IsEnabled = false;
                        try
                        {
                            var (bCnt, mCnt) = await _library.NoteExports.ExportToNotionAsync();
                            if (_closed) return;
                            SetExportStatus(mCnt > 0 ? $"✓ 已同步 {mCnt} 条笔记（{bCnt} 本书）" : L10n.T("✓ 所有划线均已同步过，已是最新"), p.Ok);
                        }
                        catch (Exception ex) { if (!_closed) SetExportStatus(ex.Message, p.Danger); }
                        finally { if (!_closed) sendBtn.IsEnabled = true; }
                    };
                    var openAppBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], HorizontalAlignment = HorizontalAlignment.Stretch, Content = "在 Notion 中打开目标页面 ↗" };
                    openAppBtn.Click += (_, _) => _ = Launcher.LaunchUriAsync(new Uri($"https://www.notion.so/{_library.Prefs.NotionPageId}"));
                    var discBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], HorizontalAlignment = HorizontalAlignment.Stretch, Foreground = ThemeHelper.Brush(p.Danger), Content = "断开 Notion" };
                    discBtn.Click += async (_, _) => await ConfirmAndDisconnectExportAsync("notion", "Notion");
                    setupStack.Children.Add(sendBtn);
                    setupStack.Children.Add(openAppBtn);
                    setupStack.Children.Add(discBtn);
                }
                break;
            }
            case "flomo":
            {
                string curUrl = _library.Secrets.Get("export.flomo.url");
                bool conn = NoteExportService.ValidateFlomoWebhook(curUrl);
                setupStack.Children.Add(new TextBlock { Text = conn ? "flomo · 已连接" : "连接 flomo", FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
                var wbBox = new TextBox { Header = "专属 API Webhook 地址（本机加密）", Text = curUrl, PlaceholderText = "https://flomoapp.com/iwh/..." };

                var flomoClipBtn = new Button
                {
                    Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    FontSize = 11,
                    Height = 25,
                    MinHeight = 25,
                    Content = "✨ 填入剪贴板中的 flomo Webhook",
                    Visibility = Visibility.Collapsed,
                };
                try
                {
                    var pkg = Clipboard.GetContent();
                    if (pkg.Contains(StandardDataFormats.Text))
                    {
                        _ = pkg.GetTextAsync().AsTask().ContinueWith(t =>
                        {
                            if (t.Status != TaskStatus.RanToCompletion) return;
                            string cand = (t.Result ?? "").Trim();
                            if (!NoteExportService.ValidateFlomoWebhook(cand) || string.Equals(cand, curUrl, StringComparison.OrdinalIgnoreCase)) return;
                            DispatcherQueue.TryEnqueue(() =>
                            {
                                if (_closed || _activePage != "export" || _focusedExportTarget != "flomo") return;
                                flomoClipBtn.Visibility = Visibility.Visible;
                                flomoClipBtn.Click += (_, _) =>
                                {
                                    wbBox.Text = cand;
                                    flomoClipBtn.Visibility = Visibility.Collapsed;
                                };
                            });
                        });
                    }
                }
                catch { }

                var saveWbBtn = new Button { Style = (Style)Application.Current.Resources["YYPrimaryPillButtonStyle"], HorizontalAlignment = HorizontalAlignment.Stretch, Content = "保存 Webhook" };
                saveWbBtn.Click += (_, _) =>
                {
                    string u = wbBox.Text.Trim();
                    if (!NoteExportService.ValidateFlomoWebhook(u))
                    {
                        SetExportStatus(L10n.T("请输入以 https://flomoapp.com/iwh/ 开头的专属 API 地址"), p.Danger);
                        return;
                    }
                    _library.Secrets.Set("export.flomo.url", u);
                    _library.Prefs.FlomoWebhookUrl = "";
                    _library.Prefs.FlomoExportOn = true;
                    _library.SavePrefs();
                    RecordChange("flomo 导出", "已连接");
                    RenderDynamicPage("export");
                    RenderInspector("export");
                };
                setupStack.Children.Add(flomoClipBtn);
                setupStack.Children.Add(wbBox);
                setupStack.Children.Add(new HyperlinkButton
                {
                    Content = "去 flomo 获取专属 API Webhook ↗",
                    FontSize = 11,
                    Padding = new Thickness(0, 1, 0, 1),
                    NavigateUri = new Uri("https://v.flomoapp.com/mine?source=incoming_webhook"),
                });
                setupStack.Children.Add(saveWbBtn);
                if (conn)
                {
                    var sendBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], HorizontalAlignment = HorizontalAlignment.Stretch, Content = "立即发送全库未同步划线" };
                    sendBtn.Click += async (_, _) =>
                    {
                        sendBtn.IsEnabled = false;
                        try
                        {
                            var (_, mCnt) = await _library.NoteExports.ExportToFlomoAsync();
                            if (_closed) return;
                            SetExportStatus(mCnt > 0 ? $"✓ 已发送 {mCnt} 条卡片到 flomo" : L10n.T("✓ 已是最新，没有未发过的新划线"), p.Ok);
                        }
                        catch (Exception ex) { if (!_closed) SetExportStatus(ex.Message, p.Danger); }
                        finally { if (!_closed) sendBtn.IsEnabled = true; }
                    };
                    var openFlomoBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], HorizontalAlignment = HorizontalAlignment.Stretch, Content = "打开 flomo ↗" };
                    openFlomoBtn.Click += (_, _) => _ = Launcher.LaunchUriAsync(new Uri("https://v.flomoapp.com"));
                    var discBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], HorizontalAlignment = HorizontalAlignment.Stretch, Foreground = ThemeHelper.Brush(p.Danger), Content = "断开 flomo" };
                    discBtn.Click += async (_, _) => await ConfirmAndDisconnectExportAsync("flomo", "flomo");
                    setupStack.Children.Add(sendBtn);
                    setupStack.Children.Add(openFlomoBtn);
                    setupStack.Children.Add(discBtn);
                }
                break;
            }
            case "xinzhi":
            {
                bool conn = !string.IsNullOrEmpty(_library.Secrets.Get("export.xinzhi.token"));
                setupStack.Children.Add(new TextBlock { Text = conn ? $"新枝 · 已连接（{_library.Prefs.XinzhiConnected}）" : "连接新枝", FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
                var xzBox = new PasswordBox { Header = "CLI Token", Password = _library.Secrets.Get("export.xinzhi.token"), PlaceholderText = "粘贴新枝 CLI Token…" };
                var verifyBtn = new Button { Style = (Style)Application.Current.Resources["YYPrimaryPillButtonStyle"], HorizontalAlignment = HorizontalAlignment.Stretch, Content = conn ? "重新验证" : "验证并连接新枝" };
                verifyBtn.Click += async (_, _) =>
                {
                    verifyBtn.IsEnabled = false;
                    SetExportStatus(L10n.T("正在验证新枝账号…"), p.T3);
                    var (ok, uName, msg) = await _library.NoteExports.VerifyXinzhiAsync(xzBox.Password);
                    if (_closed) return;
                    SetExportStatus(msg, ok ? p.Ok : p.Danger);
                    verifyBtn.IsEnabled = true;
                    if (ok)
                    {
                        _library.Secrets.Set("export.xinzhi.token", xzBox.Password.Trim());
                        _library.Prefs.XinzhiConnected = uName;
                        _library.Prefs.XinzhiExportOn = true;
                        _library.SavePrefs();
                        RecordChange("新枝导出", uName);
                        RenderDynamicPage("export");
                        RenderInspector("export");
                    }
                };
                setupStack.Children.Add(xzBox);
                setupStack.Children.Add(new HyperlinkButton
                {
                    Content = "前往新枝官网获取 CLI Token ↗",
                    FontSize = 11,
                    Padding = new Thickness(0, 1, 0, 1),
                    NavigateUri = new Uri("https://xinzhi.zone"),
                });
                setupStack.Children.Add(verifyBtn);
                if (conn)
                {
                    var sendBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], HorizontalAlignment = HorizontalAlignment.Stretch, Content = "立即发送全库未同步划线" };
                    sendBtn.Click += async (_, _) =>
                    {
                        sendBtn.IsEnabled = false;
                        try
                        {
                            var (_, mCnt) = await _library.NoteExports.ExportToXinzhiAsync();
                            if (_closed) return;
                            SetExportStatus(mCnt > 0 ? $"✓ 已发送 {mCnt} 条笔记到新枝" : L10n.T("✓ 已是最新，没有未发过的新划线"), p.Ok);
                        }
                        catch (Exception ex) { if (!_closed) SetExportStatus(ex.Message, p.Danger); }
                        finally { if (!_closed) sendBtn.IsEnabled = true; }
                    };
                    var openXzBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], HorizontalAlignment = HorizontalAlignment.Stretch, Content = "打开新枝 ↗" };
                    openXzBtn.Click += (_, _) => _ = Launcher.LaunchUriAsync(new Uri("https://xinzhi.zone"));
                    var discBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], HorizontalAlignment = HorizontalAlignment.Stretch, Foreground = ThemeHelper.Brush(p.Danger), Content = "断开新枝" };
                    discBtn.Click += async (_, _) => await ConfirmAndDisconnectExportAsync("xinzhi", "新枝");
                    setupStack.Children.Add(sendBtn);
                    setupStack.Children.Add(openXzBtn);
                    setupStack.Children.Add(discBtn);
                }
                break;
            }
            default:
            {
                // 1:1 settings-notes/1.png: unboxed Obsidian header + full-width crisp folder picker button + caption
                bool conn = !string.IsNullOrEmpty(_library.Prefs.ObsidianFolder);
                var obsHead = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
                obsHead.Children.Add(YYChrome.MakeServiceTile("obsidian", IsDark, 30));
                var obsMeta = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
                obsMeta.Children.Add(new TextBlock { Text = conn ? "Obsidian · 已连接" : "连接 Obsidian", FontSize = 13.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
                obsMeta.Children.Add(new TextBlock { Text = conn ? Path.GetFileName(_library.Prefs.ObsidianFolder) : "选择笔记库中的文件夹", FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
                obsHead.Children.Add(obsMeta);
                setupStack.Children.Add(obsHead);

                var pickSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
                pickSp.Children.Add(YYIcons.Make("folder-plus", 14, ThemeHelper.Brush(p.T1)));
                pickSp.Children.Add(new TextBlock { Text = conn ? "更换文件夹…" : "选一个文件夹…", FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1) });
                var pickBtn = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Height = 40,
                    Padding = new Thickness(12, 0, 12, 0),
                    CornerRadius = new CornerRadius(10),
                    Background = ThemeHelper.Brush(p.Card),
                    BorderBrush = ThemeHelper.Brush(p.Line),
                    BorderThickness = new Thickness(0.75),
                    UseSystemFocusVisuals = false,
                    Content = pickSp,
                };
                pickBtn.Click += async (_, _) =>
                {
                    string? folderPath = await NativePickerHelper.PickSingleFolderAsync(this);
                    if (string.IsNullOrEmpty(folderPath) || _closed) return;
                    _library.Prefs.ObsidianFolder = folderPath;
                    _library.Prefs.ObsidianExportOn = true;
                    _library.SavePrefs();
                    RecordChange("Obsidian 目录", Path.GetFileName(folderPath));
                    RenderDynamicPage("export");
                    RenderInspector("export");
                };
                setupStack.Children.Add(pickBtn);
                setupStack.Children.Add(new TextBlock { Text = conn ? _library.Prefs.ObsidianFolder : "选 Obsidian 库，或库里的一个文件夹。每本书写成一篇 Markdown，文件名就是书名，带作者和标签。", FontSize = 11, LineHeight = 17, Foreground = ThemeHelper.Brush(p.T3), TextWrapping = TextWrapping.Wrap });
                if (conn)
                {
                    var sendBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], HorizontalAlignment = HorizontalAlignment.Stretch, Content = "立即生成全部书籍 Markdown" };
                    sendBtn.Click += async (_, _) =>
                    {
                        sendBtn.IsEnabled = false;
                        try
                        {
                            var (bCnt, mCnt) = await _library.NoteExports.ExportToObsidianAsync();
                            if (_closed) return;
                            SetExportStatus(mCnt > 0 ? $"✓ 已写入 {bCnt} 本书共 {mCnt} 条笔记" : L10n.T("✓ 已是最新"), p.Ok);
                        }
                        catch (Exception ex) { if (!_closed) SetExportStatus(ex.Message, p.Danger); }
                        finally { if (!_closed) sendBtn.IsEnabled = true; }
                    };
                    var openObsBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], HorizontalAlignment = HorizontalAlignment.Stretch, Content = "在 Obsidian 中打开 ↗" };
                    openObsBtn.Click += async (_, _) =>
                    {
                        string folder = _library.Prefs.ObsidianFolder;
                        bool launched = false;
                        try
                        {
                            launched = await Launcher.LaunchUriAsync(new Uri($"obsidian://open?path={Uri.EscapeDataString(folder)}"));
                        }
                        catch { }
                        if (!launched && Directory.Exists(folder))
                            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
                    };
                    var disc = new Button { HorizontalAlignment = HorizontalAlignment.Stretch, Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Foreground = ThemeHelper.Brush(p.Danger), Content = "断开 Obsidian" };
                    disc.Click += async (_, _) => await ConfirmAndDisconnectExportAsync("obsidian", "Obsidian");
                    setupStack.Children.Add(sendBtn);
                    setupStack.Children.Add(openObsBtn);
                    setupStack.Children.Add(disc);
                }
                break;
            }
        }
        setupStack.Children.Add(statusNote);
        if (_focusedExportTarget == "obsidian")
        {
            InspectorHost.Children.Add(setupStack);
        }
        else
        {
            InspectorHost.Children.Add(new Border { CornerRadius = new CornerRadius(11), Background = ThemeHelper.Brush(p.Card), BorderBrush = ThemeHelper.Brush(p.Line), BorderThickness = new Thickness(0.5), Padding = new Thickness(12), Child = setupStack });
        }
        InspectorHost.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(0, 4, 0, 2) });

        // 下半区：4 目标专属 ExportSample 预览（1:1 settings-notes/1.png）
        string targetName = _focusedExportTarget switch { "notion" => "Notion", "flomo" => "flomo", "xinzhi" => "新枝", _ => "Obsidian" };
        var sampleHead = new Grid();
        sampleHead.Children.Add(new TextBlock { Text = "样张", FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
        sampleHead.Children.Add(new TextBlock { Text = $"发到 {targetName} 是这样", FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center });
        InspectorHost.Children.Add(sampleHead);

        var sample = new StackPanel { Spacing = 7 };
        if (_focusedExportTarget == "obsidian")
        {
            var mdMeta = new Grid();
            mdMeta.Children.Add(new TextBlock { Text = "读书笔记 / 故乡.md", FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3) });
            mdMeta.Children.Add(new TextBlock { Text = "Markdown", FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3), HorizontalAlignment = HorizontalAlignment.Right });
            sample.Children.Add(mdMeta);
        }
        if (_focusedExportTarget == "flomo")
        {
            sample.Children.Add(new TextBlock { Text = "#语阅 #故乡", FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(Color.FromArgb(255, 46, 158, 91)) });
            sample.Children.Add(new TextBlock { Text = "希望本是无所谓有，无所谓无的。这正如地上的路；其实地上本没有路，走的人多了，也便成了路。", FontSize = 12, Foreground = ThemeHelper.Brush(p.T1), TextWrapping = TextWrapping.Wrap });
            sample.Children.Add(new TextBlock { Text = "💭 这句是全篇的落点", FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T2) });
        }
        else if (_focusedExportTarget == "xinzhi")
        {
            sample.Children.Add(new TextBlock { Text = "希望本是无所谓有，无所谓无的。", FontFamily = new FontFamily(YYFontFamily.Serif.WindowsFamily()), FontSize = 12.5, Foreground = ThemeHelper.Brush(p.T1), TextWrapping = TextWrapping.Wrap });
            sample.Children.Add(new TextBlock { Text = "——《故乡》 鲁迅", FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), HorizontalAlignment = HorizontalAlignment.Right });
        }
        else
        {
            sample.Children.Add(new TextBlock { Text = "故乡", FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
            sample.Children.Add(new TextBlock { Text = "鲁迅 · 《呐喊》", FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3) });
            Color barCol = _focusedExportTarget == "obsidian" ? Color.FromArgb(175, 124, 58, 237) : tint.Accent;
            foreach (var q in new[] { "我冒了严寒，回到相隔二千余里，别了二十余年的故乡去。", "希望本是无所谓有，无所谓无的。" })
            {
                var qRow = new Grid { ColumnSpacing = 8 };
                qRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2.5) });
                qRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                qRow.Children.Add(new Border { CornerRadius = new CornerRadius(1), Background = ThemeHelper.Brush(barCol) });
                var qTb = new TextBlock { Text = q, FontFamily = new FontFamily(YYFontFamily.Serif.WindowsFamily()), FontSize = 12, Foreground = ThemeHelper.Brush(p.T1), TextWrapping = TextWrapping.Wrap };
                Grid.SetColumn(qTb, 1);
                qRow.Children.Add(qTb);
                sample.Children.Add(qRow);
            }
            if (_focusedExportTarget == "obsidian")
            {
                var tags = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                foreach (var tg in new[] { "#语阅", "#鲁迅" })
                    tags.Children.Add(new Border { CornerRadius = new CornerRadius(9), Padding = new Thickness(7, 2, 7, 2), Background = ThemeHelper.Brush(Color.FromArgb(24, 124, 58, 237)), Child = new TextBlock { Text = tg, FontSize = 10.5, Foreground = ThemeHelper.Brush(Color.FromArgb(255, 124, 58, 237)) } });
                sample.Children.Add(tags);
            }
        }

        InspectorHost.Children.Add(new Border { CornerRadius = new CornerRadius(10), Background = ThemeHelper.Brush(p.Card), BorderBrush = ThemeHelper.Brush(p.Line), BorderThickness = new Thickness(1), Padding = new Thickness(12), Child = sample });
        InspectorHost.Children.Add(new TextBlock { Text = "用《故乡》的两条划线和一条想法排的", FontSize = 11, Foreground = ThemeHelper.Brush(p.T3) });
    }

    // ---------- 8. 存储（1:1 MacSettings.swift:1248-1407） ----------

    private void BuildStoragePage()
    {
        var p = CurrentPalette;
        var tint = CurrentTint;

        var storageHost = new StackPanel { Spacing = 12 };
        storageHost.Children.Add(MakeCard(new TextBlock
        {
            Text = "正在统计本机空间占用…",
            FontSize = 12,
            Foreground = ThemeHelper.Brush(p.T3),
            Padding = new Thickness(16, 14, 16, 14),
        }));
        DynamicPageHost.Children.Add(storageHost);

        string dataDir = _library.DataDir;
        var kept = _library.GetKeptBooks();
        var keptIds = kept.Select(b => b.Id).ToHashSet(StringComparer.Ordinal);
        var audioIds = kept.Where(b => b.Format == BookFormat.Audio).Select(b => b.Id).ToHashSet(StringComparer.Ordinal);

        _ = Task.Run(() =>
        {
            long booksBytes = 0;
            long audioBytes = 0;
            long deletedBytes = 0;
            long voiceBytes = 0;
            long qaBytes = 0;
            long totalBytes = 0;
            int deletedCount = 0;
            var perBookSizes = new List<(string Title, long Bytes)>();
            var titleMap = kept.ToDictionary(b => b.Id, b => b.Title, StringComparer.Ordinal);

            static long TopFilesSize(string dir)
            {
                long sum = 0;
                try
                {
                    foreach (var f in Directory.EnumerateFiles(dir))
                    {
                        try { sum += new FileInfo(f).Length; } catch { }
                    }
                }
                catch { }
                return sum;
            }

            if (Directory.Exists(dataDir))
            {
                totalBytes += TopFilesSize(dataDir);
                try
                {
                    foreach (var topSub in Directory.EnumerateDirectories(dataDir))
                    {
                        string topName = Path.GetFileName(topSub);
                        if (topName.Equals("books", StringComparison.OrdinalIgnoreCase))
                        {
                            totalBytes += TopFilesSize(topSub);
                            try
                            {
                                foreach (var sub in Directory.EnumerateDirectories(topSub))
                                {
                                    string id = Path.GetFileName(sub);
                                    long sz = DirSize(sub);
                                    totalBytes += sz;
                                    if (keptIds.Contains(id))
                                    {
                                        if (audioIds.Contains(id)) audioBytes += sz;
                                        else booksBytes += sz;
                                        if (sz > 0 && titleMap.TryGetValue(id, out var ttl))
                                            perBookSizes.Add((ttl, sz));
                                    }
                                    else
                                    {
                                        deletedBytes += sz;
                                        deletedCount++;
                                    }
                                }
                            }
                            catch { }
                        }
                        else if (topName.Equals("audiobooks", StringComparison.OrdinalIgnoreCase) ||
                                 topName.Equals("recordings", StringComparison.OrdinalIgnoreCase))
                        {
                            long sz = DirSize(topSub);
                            audioBytes += sz;
                            totalBytes += sz;
                        }
                        else if (topName.Equals("trash", StringComparison.OrdinalIgnoreCase))
                        {
                            long sz = DirSize(topSub);
                            deletedBytes += sz;
                            totalBytes += sz;
                        }
                        else if (topName.Equals("models", StringComparison.OrdinalIgnoreCase))
                        {
                            long sz = DirSize(topSub);
                            voiceBytes += sz;
                            totalBytes += sz;
                        }
                        else if (topName.Equals("cache", StringComparison.OrdinalIgnoreCase))
                        {
                            totalBytes += TopFilesSize(topSub);
                            try
                            {
                                foreach (var cSub in Directory.EnumerateDirectories(topSub))
                                {
                                    long sz = DirSize(cSub);
                                    totalBytes += sz;
                                    if (Path.GetFileName(cSub).Equals("kokoro", StringComparison.OrdinalIgnoreCase))
                                        voiceBytes += sz;
                                }
                            }
                            catch { }
                        }
                        else if (topName.Equals("companion", StringComparison.OrdinalIgnoreCase) ||
                                 topName.Equals("companion-cache", StringComparison.OrdinalIgnoreCase))
                        {
                            long sz = DirSize(topSub);
                            qaBytes += sz;
                            totalBytes += sz;
                        }
                        else
                        {
                            totalBytes += DirSize(topSub);
                        }
                    }
                }
                catch { }
            }

            long otherBytes = Math.Max(0, totalBytes - booksBytes - audioBytes - voiceBytes - deletedBytes);
            long freeBytes = 0;
            try { freeBytes = new DriveInfo(Path.GetPathRoot(dataDir) ?? "C:\\").AvailableFreeSpace; } catch { }

            var topBooks = perBookSizes.OrderByDescending(x => x.Bytes).Take(12).ToArray();
            return (booksBytes, audioBytes, voiceBytes, deletedBytes, deletedCount, otherBytes, totalBytes, freeBytes, qaBytes, topBooks);
        }).ContinueWith(t =>
        {
            if (t.Status != TaskStatus.RanToCompletion) return;
            var s = t.Result;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_closed || _activePage != "storage") return;
                storageHost.Children.Clear();
                var pal = CurrentPalette;
                var tnt = CurrentTint;

                var barWrap = new StackPanel { Spacing = 10, Padding = new Thickness(16) };
                var headLine = new Grid();
                var usedLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                usedLine.Children.Add(new TextBlock { Text = "已用空间", FontSize = 13, Foreground = ThemeHelper.Brush(pal.T2), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 2) });
                usedLine.Children.Add(new TextBlock { Text = FormatBytes(s.totalBytes), FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = ThemeHelper.Brush(pal.T1) });
                headLine.Children.Add(usedLine);
                if (s.freeBytes > 0)
                    headLine.Children.Add(new TextBlock { Text = $"本机可用 {FormatBytes(s.freeBytes)}", FontSize = 11.5, Foreground = ThemeHelper.Brush(pal.T3), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom });
                barWrap.Children.Add(headLine);

                // 5 段彩条：书文件 / 有声书录音 / 语音模型 / 已删除 / 其他（1:1 settings-storage/1.png 圆角浅灰底槽）
                Color otherCol = IsDark ? Color.FromArgb(110, 180, 176, 170) : Color.FromArgb(128, 198, 194, 188);
                (string Label, long Bytes, Color Col)[] parts =
                [
                    ("书文件", s.booksBytes, tnt.Fill),
                    ("有声书录音", s.audioBytes, Color.FromArgb(205, tnt.Fill.R, tnt.Fill.G, tnt.Fill.B)),
                    ("语音模型", s.voiceBytes, Color.FromArgb(145, tnt.Fill.R, tnt.Fill.G, tnt.Fill.B)),
                    ("已删除", s.deletedBytes, pal.Danger),
                    ("其他", s.otherBytes, otherCol),
                ];

                var barGrid = new Grid { Height = 8, ColumnSpacing = 2 };
                long sum = Math.Max(1, parts.Sum(x => x.Bytes));
                int colIdx = 0;
                var activeParts = parts.Where(x => x.Bytes > 0).ToArray();
                for (int pi = 0; pi < activeParts.Length; pi++)
                {
                    var pt = activeParts[pi];
                    barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.03, (double)pt.Bytes / sum), GridUnitType.Star) });
                    double leftR = pi == 0 ? 4 : 1.5;
                    double rightR = pi == activeParts.Length - 1 ? 4 : 1.5;
                    var seg = new Border { Background = ThemeHelper.Brush(pt.Col), CornerRadius = new CornerRadius(leftR, rightR, rightR, leftR) };
                    Grid.SetColumn(seg, colIdx++);
                    barGrid.Children.Add(seg);
                }
                barWrap.Children.Add(new Border
                {
                    Height = 8,
                    CornerRadius = new CornerRadius(4),
                    Background = ThemeHelper.Brush(pal.Inset),
                    Child = barGrid,
                });

                var legend = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
                foreach (var pt in activeParts)
                {
                    var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
                    item.Children.Add(new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(3.5), Background = ThemeHelper.Brush(pt.Col), VerticalAlignment = VerticalAlignment.Center });
                    item.Children.Add(new TextBlock { Text = $"{pt.Label} {FormatBytes(pt.Bytes)}", FontSize = 10.5, Foreground = ThemeHelper.Brush(pal.T2) });
                    legend.Children.Add(item);
                }
                barWrap.Children.Add(legend);

                int deletedCount = s.deletedCount;
                var purgeBtn = YYChrome.MakeSettingsActionButton("立即清空", async () =>
                {
                    bool ok = await YYChrome.ShowConfirmAsync(
                        RootGrid.XamlRoot,
                        IsDark,
                        L10n.T("清空最近删除？"),
                        L10n.T("清空后无法再撤销找回已删的书籍文件。"),
                        confirmText: L10n.T("立即清空"),
                        cancelText: L10n.T("取消"),
                        destructive: true,
                        tint: _library.Prefs.Tint);
                    if (ok && !_closed)
                    {
                        try
                        {
                            await Task.Run(() => _library.Core.PurgeDeleted(0));
                            if (_closed) return;
                            RecordChange("最近删除", "已清空");
                            if (_activePage == "storage") RenderDynamicPage("storage");
                        }
                        catch { }
                    }
                }, IsDark, destructive: true, enabled: s.deletedBytes > 0 || deletedCount > 0);

                var clearQaRight = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
                clearQaRight.Children.Add(new TextBlock { Text = FormatBytes(s.qaBytes), FontSize = 12, Foreground = ThemeHelper.Brush(pal.T2), VerticalAlignment = VerticalAlignment.Center });
                clearQaRight.Children.Add(YYChrome.MakeSettingsActionButton("清理", async () =>
                {
                    bool ok = await YYChrome.ShowConfirmAsync(
                        RootGrid.XamlRoot,
                        IsDark,
                        L10n.T("清理书伴问答缓存？"),
                        L10n.T($"将释放约 {FormatBytes(s.qaBytes)}，不影响已保存的划线和笔记。"),
                        confirmText: L10n.T("清理"),
                        cancelText: L10n.T("取消"),
                        destructive: true,
                        tint: _library.Prefs.Tint);
                    if (!ok || _closed) return;
                    await Task.Run(() =>
                    {
                        foreach (var dName in new[] { "companion", "companion-cache" })
                        {
                            try
                            {
                                string dir = Path.Combine(dataDir, dName);
                                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                            }
                            catch { }
                        }
                    });
                    if (_closed) return;
                    RecordChange("书伴问答缓存", "已清理");
                    if (_activePage == "storage") RenderDynamicPage("storage");
                }, IsDark, destructive: true, enabled: s.qaBytes > 0));

                var cleanupRows = new List<UIElement>
                {
                    barWrap,
                    MakeRow($"最近删除 · {deletedCount} 本 ({FormatBytes(s.deletedBytes)})", "保留 30 天以便恢复，到期自动清理", purgeBtn),
                };
                if (s.voiceBytes > 0)
                {
                    var rmModelBtn = YYChrome.MakeSettingsActionButton("移除", async () =>
                    {
                        bool ok = await YYChrome.ShowConfirmAsync(
                            RootGrid.XamlRoot,
                            IsDark,
                            L10n.T("移除本地语音模型？"),
                            L10n.T($"将释放 {FormatBytes(s.voiceBytes)}，之后如需使用本地真人朗读或声音克隆需要重新下载。"),
                            confirmText: L10n.T("移除"),
                            cancelText: L10n.T("取消"),
                            destructive: true,
                            tint: _library.Prefs.Tint);
                        if (!ok || _closed) return;
                        await Task.Run(() =>
                        {
                            _library.Kokoro.DeleteInstalledModel();
                            _library.LiveVoice.DeleteInstalledModels();
                        });
                        if (_closed) return;
                        RecordChange("语音合成模型", "已移除");
                        if (_activePage == "storage") RenderDynamicPage("storage");
                    }, IsDark, destructive: true);
                    cleanupRows.Add(MakeRow("语音合成模型", FormatBytes(s.voiceBytes), rmModelBtn));
                }
                cleanupRows.Add(MakeRow("书伴问答缓存", "同章节重复提问直接读取本地缓存，不重复调用模型", clearQaRight));
                storageHost.Children.Add(MakeCard(cleanupRows.ToArray()));

                if (s.topBooks.Length > 0)
                {
                    storageHost.Children.Add(MakeHeading(L10n.T("各书占用空间")));
                    storageHost.Children.Add(MakeCard(s.topBooks.Select(b =>
                        (UIElement)L10n.Skip(MakeRow(b.Title, null, new TextBlock { Text = FormatBytes(b.Bytes), FontSize = 12, Foreground = ThemeHelper.Brush(pal.T2) }, compact: true))).ToArray()));
                }

                // 可选离线扩展包（放在「各书占用空间」之后，保持首屏 1:1 对齐 settings-storage/1.png）
                var extRows = new List<UIElement>();
                bool alignerInstalled = Aligner.IsInstalled(dataDir);
                bool alignerDownloading = !alignerInstalled && _alignerDlCts is { IsCancellationRequested: false };
                var alignerBtn = new Button
                {
                    Style = (Style)Application.Current.Resources[alignerInstalled ? "YYIconButtonStyle" : "YYPillButtonStyle"],
                    Content = alignerInstalled
                        ? "移除"
                        : (alignerDownloading ? $"{L10n.T("取消")} ({Math.Round(_alignerDlProgress * 100)}%)" : "下载 (~303 MB)"),
                    Foreground = alignerInstalled ? ThemeHelper.Brush(pal.Danger) : ThemeHelper.Brush(pal.T1),
                    FontSize = 11.5,
                };
                _alignerProgressUiUpdater = () =>
                {
                    if (_closed || _activePage != "storage") return;
                    if (_alignerDlCts is { IsCancellationRequested: false })
                        alignerBtn.Content = $"{L10n.T("取消")} ({Math.Round(_alignerDlProgress * 100)}%)";
                };
                alignerBtn.Click += async (_, _) =>
                {
                    if (_alignerDlCts is { IsCancellationRequested: false })
                    {
                        try { _alignerDlCts.Cancel(); } catch { }
                        return;
                    }
                    if (Aligner.IsInstalled(dataDir))
                    {
                        bool ok = await YYChrome.ShowConfirmAsync(
                            RootGrid.XamlRoot,
                            IsDark,
                            L10n.T("移除有声书强制对齐模型？"),
                            L10n.T("移除后将释放约 303 MB 空间，之后按需可随时重新下载。"),
                            confirmText: L10n.T("移除"),
                            cancelText: L10n.T("取消"),
                            destructive: true,
                            tint: _library.Prefs.Tint);
                        if (!ok || _closed) return;
                        await Task.Run(() => Aligner.DeleteModel(dataDir));
                        if (_closed) return;
                        RecordChange("对齐模型", "已移除");
                        if (_activePage == "storage") RenderDynamicPage("storage");
                        return;
                    }

                    try { _alignerDlCts?.Cancel(); _alignerDlCts?.Dispose(); } catch { }
                    var cts = new CancellationTokenSource();
                    _alignerDlCts = cts;
                    _alignerDlProgress = 0;
                    alignerBtn.Content = $"{L10n.T("取消")} (0%)";
                    try
                    {
                        await Aligner.EnsureAsync(dataDir, frac =>
                        {
                            _alignerDlProgress = frac;
                            DispatcherQueue.TryEnqueue(() =>
                            {
                                if (_closed) return;
                                _alignerProgressUiUpdater?.Invoke();
                            });
                        }, cts.Token);
                        if (_closed) return;
                        RecordChange("对齐模型", "已安装");
                        if (_activePage == "storage") RenderDynamicPage("storage");
                    }
                    catch (OperationCanceledException)
                    {
                        if (!_closed && _activePage == "storage")
                            alignerBtn.Content = L10n.T("下载 (~303 MB)");
                    }
                    catch (Exception ex)
                    {
                        if (!_closed)
                        {
                            if (_activePage == "storage") alignerBtn.Content = L10n.T("重试下载");
                            RecordChange("对齐模型", ex.Message);
                        }
                    }
                    finally
                    {
                        if (ReferenceEquals(_alignerDlCts, cts))
                        {
                            try { cts.Dispose(); } catch { }
                            _alignerDlCts = null;
                            _alignerDlProgress = 0;
                        }
                    }
                };
                extRows.Add(MakeRow(
                    "有声书强制对齐模型",
                    alignerInstalled ? "已安装 (mms_fa.int8.onnx · 约 303 MB)" : "本地离线的 MMS 听读自动对齐模型，按需下载",
                    alignerBtn));

                bool svInstalled = SenseVoiceListener.IsInstalled(dataDir);
                bool svDownloading = !svInstalled && _senseVoiceDlCts is { IsCancellationRequested: false };
                var svBtn = new Button
                {
                    Style = (Style)Application.Current.Resources[svInstalled ? "YYIconButtonStyle" : "YYPillButtonStyle"],
                    Content = svInstalled
                        ? "移除"
                        : (svDownloading ? $"{L10n.T("取消")} ({Math.Round(_senseVoiceDlProgress * 100)}%)" : "下载 (~228 MB)"),
                    Foreground = svInstalled ? ThemeHelper.Brush(pal.Danger) : ThemeHelper.Brush(pal.T1),
                    FontSize = 11.5,
                };
                _senseVoiceProgressUiUpdater = () =>
                {
                    if (_closed || _activePage != "storage") return;
                    if (_senseVoiceDlCts is { IsCancellationRequested: false })
                        svBtn.Content = $"{L10n.T("取消")} ({Math.Round(_senseVoiceDlProgress * 100)}%)";
                };
                svBtn.Click += async (_, _) =>
                {
                    if (_senseVoiceDlCts is { IsCancellationRequested: false })
                    {
                        try { _senseVoiceDlCts.Cancel(); } catch { }
                        return;
                    }
                    if (SenseVoiceListener.IsInstalled(dataDir))
                    {
                        bool ok = await YYChrome.ShowConfirmAsync(
                            RootGrid.XamlRoot,
                            IsDark,
                            L10n.T("移除离线语音转写模型？"),
                            L10n.T("移除后将释放约 228 MB 空间，之后按需可随时重新下载。"),
                            confirmText: L10n.T("移除"),
                            cancelText: L10n.T("取消"),
                            destructive: true,
                            tint: _library.Prefs.Tint);
                        if (!ok || _closed) return;
                        await Task.Run(() => SenseVoiceListener.DeleteModel(dataDir));
                        if (_closed) return;
                        RecordChange("离线转写模型", "已移除");
                        if (_activePage == "storage") RenderDynamicPage("storage");
                        return;
                    }

                    try { _senseVoiceDlCts?.Cancel(); _senseVoiceDlCts?.Dispose(); } catch { }
                    var cts = new CancellationTokenSource();
                    _senseVoiceDlCts = cts;
                    _senseVoiceDlProgress = 0;
                    svBtn.Content = $"{L10n.T("取消")} (0%)";
                    try
                    {
                        await SenseVoiceListener.EnsureAsync(dataDir, frac =>
                        {
                            _senseVoiceDlProgress = frac;
                            DispatcherQueue.TryEnqueue(() =>
                            {
                                if (_closed) return;
                                _senseVoiceProgressUiUpdater?.Invoke();
                            });
                        }, cts.Token);
                        if (_closed) return;
                        RecordChange("离线转写模型", "已安装");
                        if (_activePage == "storage") RenderDynamicPage("storage");
                    }
                    catch (OperationCanceledException)
                    {
                        if (!_closed && _activePage == "storage")
                            svBtn.Content = L10n.T("下载 (~228 MB)");
                    }
                    catch (Exception ex)
                    {
                        if (!_closed)
                        {
                            if (_activePage == "storage") svBtn.Content = L10n.T("重试下载");
                            RecordChange("离线转写模型", ex.Message);
                        }
                    }
                    finally
                    {
                        if (ReferenceEquals(_senseVoiceDlCts, cts))
                        {
                            try { cts.Dispose(); } catch { }
                            _senseVoiceDlCts = null;
                            _senseVoiceDlProgress = 0;
                        }
                    }
                };
                extRows.Add(MakeRow(
                    "离线语音转写模型 (SenseVoice)",
                    svInstalled ? "已安装 (sense_voice.int8.onnx · 约 228 MB)" : "中英日韩粤高精度本机逐字稿与有声书转文字模型，按需下载",
                    svBtn));

                bool soundsInstalled = SoundLibraryService.IsInstalled(dataDir);
                var soundsBtn = new Button
                {
                    Style = (Style)Application.Current.Resources[soundsInstalled ? "YYIconButtonStyle" : "YYPillButtonStyle"],
                    Content = soundsInstalled ? "移除" : "导入素材包…",
                    Foreground = soundsInstalled ? ThemeHelper.Brush(pal.Danger) : ThemeHelper.Brush(pal.T1),
                    FontSize = 11.5,
                };
                soundsBtn.Click += async (_, _) =>
                {
                    if (SoundLibraryService.IsInstalled(dataDir))
                    {
                        bool ok = await YYChrome.ShowConfirmAsync(
                            RootGrid.XamlRoot,
                            IsDark,
                            L10n.T("移除广播剧音效素材库？"),
                            L10n.T("移除后将自动切回内置合成环境音。"),
                            confirmText: L10n.T("移除"),
                            cancelText: L10n.T("取消"),
                            destructive: true,
                            tint: _library.Prefs.Tint);
                        if (!ok || _closed) return;
                        await Task.Run(() => SoundLibraryService.DeleteLibrary(dataDir));
                        if (_closed) return;
                        RecordChange("音效素材库", "已移除");
                        if (_activePage == "storage") RenderDynamicPage("storage");
                        return;
                    }
                    string? zipPath = await NativePickerHelper.PickSingleFileAsync(this, [".zip"]);
                    if (string.IsNullOrEmpty(zipPath)) return;
                    try
                    {
                        uint count = await SoundLibraryService.ImportLocalZipAsync(_library.Core, dataDir, zipPath);
                        RecordChange("音效素材库", $"已加载 {count} 条素材");
                        RenderDynamicPage("storage");
                    }
                    catch (Exception ex)
                    {
                        await YYChrome.ShowNoticeAsync(
                            RootGrid.XamlRoot,
                            IsDark,
                            L10n.T("导入音效素材库失败"),
                            ex.Message,
                            okText: L10n.T("好"),
                            tint: _library.Prefs.Tint);
                    }
                };
                extRows.Add(MakeRow(
                    "广播剧音效素材库",
                    soundsInstalled ? "已安装 (sounds-1) · 未安装时自动使用核心内置合成环境音" : "可选扩展（未安装时使用内置环境音与音效混音）",
                    soundsBtn));

                storageHost.Children.Add(MakeHeading("离线扩展包"));
                storageHost.Children.Add(MakeCard(extRows.ToArray()));
                L10n.ApplyTree(storageHost);
            });
        });
    }

    // ---------- 9. 数据（1:1 MacSettings.swift:1131-1244 & SnapshotSheets.swift） ----------

    private void BuildDataPage()
    {
        var p = CurrentPalette;
        var tint = CurrentTint;
        string snapDir = Path.Combine(_library.DataDir, "snapshots");
        Directory.CreateDirectory(snapDir);

        DynamicPageHost.Children.Add(MakeHeading("生成快照"));

        SnapshotCounts? loadedCounts = null;
        var readSubTb = new TextBlock { Text = L10n.T("正在统计…"), FontSize = 11, Foreground = ThemeHelper.Brush(p.T3) };
        var notesSubTb = new TextBlock { Text = L10n.T("正在统计…"), FontSize = 11, Foreground = ThemeHelper.Brush(p.T3) };
        var wordsSubTb = new TextBlock { Text = L10n.T("正在统计…"), FontSize = 11, Foreground = ThemeHelper.Brush(p.T3) };
        var filesSubTb = new TextBlock { Text = L10n.T("正在统计…"), FontSize = 11, Foreground = ThemeHelper.Brush(p.T3) };

        var estText = new TextBlock { FontSize = 12, Foreground = ThemeHelper.Brush(p.T2), VerticalAlignment = VerticalAlignment.Center };
        var importExtBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "导入快照…" };
        importExtBtn.Click += async (_, _) =>
        {
            if (await SnapshotAndConflictDialogs.ShowSnapshotImportDialogAsync(this, RootGrid.XamlRoot, _library))
            {
                RenderDynamicPage("data");
                RenderInspector("data");
            }
        };

        var makeBtn = new Button
        {
            Style = (Style)Application.Current.Resources["YYPrimaryPillButtonStyle"],
            Content = "生成快照",
        };

        void RefreshEstimate()
        {
            bool anySelected = _snapProgress || _snapNotes || _snapWords || _snapSettings || _snapFiles;
            makeBtn.IsEnabled = anySelected;
            if (!anySelected)
            {
                estText.Text = L10n.T("请至少勾选一项要备份的内容");
                estText.Foreground = ThemeHelper.Brush(p.T3);
                return;
            }
            estText.Foreground = ThemeHelper.Brush(p.T2);
            if (loadedCounts is not { } c)
            {
                estText.Text = _snapFiles ? L10n.T("含书文件 · 正在估算大小…") : L10n.T("不含书文件 · 正在估算大小…");
                return;
            }
            bool anyMeta = _snapProgress || _snapNotes || _snapWords || _snapSettings;
            long est = (anyMeta ? Math.Max(4096L, (long)Math.Round(0.15 * c.DataBytes)) : 0L)
                       + (_snapFiles ? (long)c.FileBytes : 0L);
            estText.Text = _snapFiles
                ? $"{L10n.T("含书文件")} · {L10n.T("约")} {FormatBytes(est)}"
                : $"{L10n.T("不含书文件")} · {L10n.T("约")} {FormatBytes(est)}";
        }
        RefreshEstimate();

        makeBtn.Click += async (_, _) =>
        {
            if (!(_snapProgress || _snapNotes || _snapWords || _snapSettings || _snapFiles)) return;
            makeBtn.IsEnabled = false;
            importExtBtn.IsEnabled = false;
            makeBtn.Content = L10n.T("正在打包快照…");
            estText.Text = L10n.T("正在打包快照…");
            try
            {
                string filePath = Path.Combine(snapDir, $"语阅快照-{DateTime.Now:yyyyMMdd-HHmmss}.yuyue");
                var parts = new SnapshotParts(_snapProgress, _snapNotes, _snapWords, _snapSettings, _snapFiles);
                ulong bytes = await Task.Run(() => _library.MakeSnapshot(filePath, parts));
                if (_closed) return;
                RecordChange("备份快照", FormatBytes((long)bytes));
                if (_activePage == "data")
                {
                    RenderDynamicPage("data");
                    RenderInspector("data");
                }
            }
            catch (Exception ex)
            {
                if (_closed) return;
                importExtBtn.IsEnabled = true;
                makeBtn.Content = L10n.T("生成快照");
                RefreshEstimate();
                await ShowInfoDialogAsync("生成快照失败", ex.Message);
            }
        };

        Grid MakeAsyncCountRow(string title, TextBlock? subTb, ToggleSwitch sw)
        {
            var left = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(new TextBlock { Text = title, FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1) });
            if (subTb is not null) left.Children.Add(subTb);
            var g = new Grid { ColumnSpacing = 12, Padding = new Thickness(16, subTb is null ? 6 : 7, 16, subTb is null ? 6 : 7), MinHeight = 38 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(left);
            if (string.IsNullOrEmpty(AutomationProperties.GetName(sw)))
                AutomationProperties.SetName(sw, L10n.T(title));
            sw.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(sw, 1);
            g.Children.Add(sw);
            return g;
        }

        // 1:1 settings-data/1.png: 导入快照… + 生成快照 side-by-side in the snapshot card footer
        var actionRow = new Grid { Padding = new Thickness(16, 11, 16, 11) };
        actionRow.Children.Add(estText);
        var btnPair = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        btnPair.Children.Add(importExtBtn);
        btnPair.Children.Add(makeBtn);
        actionRow.Children.Add(btnPair);

        DynamicPageHost.Children.Add(MakeCard(
            MakeAsyncCountRow("进度和书签", readSubTb, MakeSwitch(_snapProgress, v => { _snapProgress = v; RefreshEstimate(); })),
            MakeAsyncCountRow("笔记", notesSubTb, MakeSwitch(_snapNotes, v => { _snapNotes = v; RefreshEstimate(); })),
            MakeAsyncCountRow("生词", wordsSubTb, MakeSwitch(_snapWords, v => { _snapWords = v; RefreshEstimate(); })),
            MakeAsyncCountRow("设置和替换规则", null, MakeSwitch(_snapSettings, v => { _snapSettings = v; RefreshEstimate(); })),
            MakeAsyncCountRow("书文件", filesSubTb, MakeSwitch(_snapFiles, v => { _snapFiles = v; RefreshEstimate(); })),
            actionRow));

        var csvBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "导出…" };
        csvBtn.Click += async (_, _) => await SnapshotAndConflictDialogs.ShowReadingExportSheetAsync(this, RootGrid.XamlRoot, _library);

        DynamicPageHost.Children.Add(MakeCard(
            MakeRow("导出阅读记录", "导出想读、在读、读完状态与完成日期（CSV），可导入 Goodreads 或 StoryGraph", csvBtn)));

        // 历史快照宿主（异步加载 SnapshotCounts + PeekSnapshot 元数据，避免阻塞 UI 线程）
        var savedSnapsHost = new StackPanel { Spacing = 8 };
        DynamicPageHost.Children.Add(savedSnapsHost);
        DynamicPageHost.Children.Add(MakeNote("快照为单个 .yuyue 文件，在 Mac、iPhone、Android 与 Windows 上打开即可恢复书库。"));

        _ = Task.Run(() =>
        {
            SnapshotCounts c = _library.GetSnapshotCounts();
            var snaps = Directory.Exists(snapDir)
                ? Directory.GetFiles(snapDir, "*.yuyue")
                    .Select(f =>
                    {
                        var fi = new FileInfo(f);
                        SnapshotAbout? about = null;
                        try { about = _library.Core.PeekSnapshot(fi.FullName); } catch { }
                        long sortMs = about?.MadeAt ?? new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds();
                        return (File: fi, About: about, SortMs: sortMs);
                    })
                    .OrderByDescending(x => x.SortMs)
                    .ToArray()
                : [];
            return (Counts: c, Snaps: snaps);
        }).ContinueWith(t =>
        {
            if (t.Status != TaskStatus.RanToCompletion) return;
            var (c, savedSnaps) = t.Result;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_closed || _activePage != "data") return;
                loadedCounts = c;
                readSubTb.Text = L10n.T($"读过 {c.Read} 本");
                notesSubTb.Text = L10n.T($"{c.Notes} 条");
                wordsSubTb.Text = L10n.T($"{c.Words} 个词");
                filesSubTb.Text = FormatBytes((long)c.FileBytes);
                RefreshEstimate();

                savedSnapsHost.Children.Clear();
                if (savedSnaps.Length == 0) return;

                savedSnapsHost.Children.Add(MakeHeading("历史快照"));
                var snapRows = new List<UIElement>();
                foreach (var item in savedSnaps)
                {
                    var fi = item.File;
                    var about = item.About;
                    string fullPath = fi.FullName;

                    DateTime madeLocal = about is not null && about.MadeAt > 0
                        ? DateTimeOffset.FromUnixTimeMilliseconds(about.MadeAt).LocalDateTime
                        : fi.LastWriteTime;
                    string whenTitle = madeLocal.Year == DateTime.Now.Year
                        ? $"{madeLocal.Month} 月 {madeLocal.Day} 日 {madeLocal:HH:mm}"
                        : $"{madeLocal.Year} 年 {madeLocal.Month} 月 {madeLocal.Day} 日 {madeLocal:HH:mm}";

                    string rawDevice = !string.IsNullOrWhiteSpace(about?.DeviceName) ? about!.DeviceName : Environment.MachineName;
                    string devLabel = string.Equals(rawDevice, Environment.MachineName, StringComparison.OrdinalIgnoreCase)
                        ? $"这台电脑（{rawDevice}）"
                        : rawDevice;
                    long byteCount = about is not null && about.Bytes > 0 ? (long)about.Bytes : fi.Length;
                    bool hasFiles = about?.Parts.Files == true;
                    string subDetail = about is not null
                        ? $"{devLabel} · 进度 {about.Counts.Read} 本 · 笔记 {about.Counts.Notes} 条 · {FormatBytes(byteCount)}"
                        : $"{devLabel} · {FormatBytes(byteCount)}";

                    async Task ExportSnapshotAsync()
                    {
                        string? destPath = await NativePickerHelper.PickSaveFileAsync(
                            this,
                            Path.GetFileNameWithoutExtension(fullPath),
                            [(L10n.T("语阅数据快照"), [".yuyue"])]);
                        if (string.IsNullOrEmpty(destPath)) return;
                        await Task.Run(() => File.Copy(fullPath, destPath, overwrite: true));
                        ShowFlashBanner("✓ 已导出快照");
                    }

                    async Task ConfirmDeleteSnapshotAsync()
                    {
                        bool ok = await YYChrome.ShowConfirmAsync(
                            RootGrid.XamlRoot,
                            IsDark,
                            L10n.T("删除这份快照？"),
                            L10n.T($"{whenTitle} 生成的快照文件会从本机删除，当前书库里的书籍和笔记不受影响。"),
                            confirmText: L10n.T("删除"),
                            cancelText: L10n.T("取消"),
                            destructive: true,
                            tint: _library.Prefs.Tint);
                        if (!ok) return;
                        try { File.Delete(fullPath); } catch { }
                        RecordChange("删除快照", whenTitle);
                        RenderDynamicPage("data");
                        RenderInspector("data");
                    }

                    var impBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "导入", Height = 26, MinHeight = 26 };
                    impBtn.Click += async (_, _) =>
                    {
                        if (await SnapshotAndConflictDialogs.ShowSnapshotImportDialogAsync(this, RootGrid.XamlRoot, _library, fullPath))
                        {
                            RenderDynamicPage("data");
                            RenderInspector("data");
                        }
                    };
                    var expBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "导出…", Height = 26, MinHeight = 26 };
                    expBtn.Click += async (_, _) => await ExportSnapshotAsync();

                    var rightActs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
                    if (hasFiles)
                        rightActs.Children.Add(MakeStatusPill("含书文件", tint.Accent));
                    rightActs.Children.Add(impBtn);
                    rightActs.Children.Add(expBtn);

                    var rowEl = MakeRow(whenTitle, subDetail, rightActs, compact: true);
                    var ctx = new MenuFlyout();
                    var expMi = new MenuFlyoutItem { Text = L10n.T("导出…") };
                    expMi.Click += async (_, _) => await ExportSnapshotAsync();
                    var revMi = new MenuFlyoutItem { Text = L10n.T("在资源管理器中显示") };
                    revMi.Click += (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{fullPath}\"") { UseShellExecute = true });
                    var delMi = new MenuFlyoutItem { Text = L10n.T("删除…"), Foreground = ThemeHelper.Brush(p.Danger) };
                    delMi.Click += async (_, _) => await ConfirmDeleteSnapshotAsync();
                    ctx.Items.Add(expMi);
                    ctx.Items.Add(revMi);
                    ctx.Items.Add(new MenuFlyoutSeparator());
                    ctx.Items.Add(delMi);
                    rowEl.ContextFlyout = ctx;
                    snapRows.Add(rowEl);
                }
                savedSnapsHost.Children.Add(MakeCard(snapRows.ToArray()));
                L10n.ApplyTree(savedSnapsHost);
            });
        });
    }

    // ---------- 10. 关于（1:1 MacUpdate.swift & UpdateUI.swift：68x68 YuyueMark + 语阅 + PencilSignature + 内嵌更新卡片 + 更新渠道） ----------

    private void BuildAboutPage()
    {
        var p = CurrentPalette;
        var tint = CurrentTint;
        var serif = new FontFamily(YYFontFamily.Serif.WindowsFamily());
        var mono = new FontFamily(YYFontFamily.Mono.WindowsFamily());
        bool preferCn = ModelDownload.PrefersChinaMirror();

        var bookplate = new Grid { ColumnSpacing = 36, Margin = new Thickness(6, 18, 0, 8), MinHeight = 470 };
        bookplate.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(212) });
        bookplate.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // 左页：藏书票书扉（framed ex-libris bookplate folio + 64x64 YuyueMark + 语阅 + 版本 + 铅笔手写签名 + GPL-3.0 徽章）
        var leftGrid = new Grid();
        leftGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        leftGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var leftTop = new StackPanel { Spacing = 0 };
        var markInner = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        markInner.Children.Add(L10n.Skip(new TextBlock
        {
            Text = "阅",
            FontFamily = serif,
            FontSize = 31,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = ThemeHelper.Brush(Color.FromArgb(255, 28, 29, 32)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, -2, 0, 0),
        }));
        markInner.Children.Add(new Border
        {
            Width = 22,
            Height = 3,
            CornerRadius = new CornerRadius(1.5),
            Background = ThemeHelper.Brush(Color.FromArgb(255, 43, 76, 126)),
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        var yuyueMark = new Border
        {
            Width = 64,
            Height = 64,
            CornerRadius = new CornerRadius(15),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = ThemeHelper.Brush(Colors.White),
            BorderBrush = ThemeHelper.Brush(Color.FromArgb(14, 0, 0, 0)),
            BorderThickness = new Thickness(0.5),
            Child = markInner,
        };
        ThemeHelper.ApplyElevation(yuyueMark, RootGrid, 14f);
        leftTop.Children.Add(yuyueMark);

        leftTop.Children.Add(new TextBlock
        {
            Text = "语阅",
            FontFamily = serif,
            FontSize = 28,
            CharacterSpacing = L10n.IsEnglish ? 25 : 140,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = ThemeHelper.Brush(p.T1),
            Margin = new Thickness(0, 16, 0, 4),
        });
        leftTop.Children.Add(new TextBlock
        {
            Text = L10n.IsEnglish ? "语阅 · for Windows" : "Yuyue for Windows",
            FontFamily = new FontFamily(YYFontFamily.LatinSerif.WindowsFamily()),
            FontStyle = Windows.UI.Text.FontStyle.Italic,
            FontSize = 13,
            Foreground = ThemeHelper.Brush(p.T3),
        });
        leftTop.Children.Add(new TextBlock
        {
            Text = $"v{UpdateAndFeedbackService.CurrentVersion} ({UpdateAndFeedbackService.CurrentBuild})",
            FontFamily = mono,
            FontSize = 11,
            Foreground = ThemeHelper.Brush(p.T3),
            Margin = new Thickness(0, 4, 0, 0),
        });
        leftTop.Children.Add(new Border
        {
            Width = 24,
            Height = 1.5,
            Background = ThemeHelper.Brush(p.Line),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 16, 0, 14),
        });
        leftTop.Children.Add(new TextBlock
        {
            Text = "读着听，听着读，进度接得上。\n没有账号与广告，\n书与笔记都留在你手里。",
            FontFamily = serif,
            FontSize = 13.5,
            LineHeight = 23,
            Foreground = ThemeHelper.Brush(p.T2),
            TextWrapping = TextWrapping.Wrap,
        });
        leftGrid.Children.Add(leftTop);

        // 底部 PencilSignature（点击签名区或底部版权行一笔笔写出 CTT 铅笔签名，1:1 PhoneSignature.swift & MacUpdate.swift:214-239）
        var (sigView, triggerSigWrite) = YYChrome.MakePencilSignature(58, IsDark);
        var sigWrap = new StackPanel { Spacing = 5, Margin = new Thickness(0, 20, 0, 0), Background = new SolidColorBrush(Colors.Transparent) };
        sigWrap.PointerPressed += (_, _) => triggerSigWrite();
        sigWrap.Children.Add(sigView);
        var leftFoot = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Background = new SolidColorBrush(Colors.Transparent) };
        leftFoot.Children.Add(new TextBlock { Text = "by", FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Center });
        var authorText = new TextBlock { Text = "nowtin", FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1) };
        var authorBtn = new Button { Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0), Padding = new Thickness(2, 0, 2, 0), MinWidth = 0, MinHeight = 0, Content = authorText, UseSystemFocusVisuals = false };
        AutomationProperties.SetName(authorBtn, "nowtin");
        authorBtn.PointerEntered += (_, _) => authorText.TextDecorations = Windows.UI.Text.TextDecorations.Underline;
        authorBtn.PointerExited += (_, _) => authorText.TextDecorations = Windows.UI.Text.TextDecorations.None;
        string authorUrl = preferCn ? "https://cnb.cool/inokoru" : "https://github.com/nowtin";
        authorBtn.Click += (_, _) => _ = Launcher.LaunchUriAsync(new Uri(authorUrl));
        leftFoot.Children.Add(authorBtn);
        leftFoot.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(5, 1, 5, 1),
            Background = ThemeHelper.Brush(p.Inset),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0.5),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = "GPL-3.0", FontSize = 10, Foreground = ThemeHelper.Brush(p.T3) },
        });
        leftFoot.PointerPressed += (_, e) => { triggerSigWrite(); e.Handled = true; };
        sigWrap.Children.Add(leftFoot);
        Grid.SetRow(sigWrap, 1);
        leftGrid.Children.Add(sigWrap);

        var leftBookplateCard = new Border
        {
            CornerRadius = new CornerRadius(14),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0.75),
            Padding = new Thickness(18, 20, 18, 16),
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = leftGrid,
        };
        bookplate.Children.Add(leftBookplateCard);

        // 右页：牌记与操作卡片（MaxWidth = 480 对齐 MacUpdate.swift rightFolio 468px）
        var rightStack = new StackPanel
        {
            Spacing = 12,
            Margin = new Thickness(0, 2, 0, 0),
            MaxWidth = 480,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        bool showActiveOfferCard = _library.Updates.LatestOffer is not null && !_library.Updates.IsOfferSuppressed;

        // Inline active update offer card (MacUpdate.swift:252-254, 418-496)
        if (_library.Updates.LatestOffer is { } activeOffer && !_library.Updates.IsOfferSuppressed)
        {
            var offerStack = new StackPanel { Spacing = 10, Padding = new Thickness(16, 14, 16, 14) };
            var offerHead = new Grid { ColumnSpacing = 8 };
            offerHead.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            offerHead.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            offerHead.Children.Add(new TextBlock
            {
                Text = $"语阅 v{activeOffer.Version}",
                FontFamily = serif,
                FontSize = 17,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = ThemeHelper.Brush(p.T1),
                VerticalAlignment = VerticalAlignment.Center,
            });
            var readyPill = MakeStatusPill("可以安装", p.Ok);
            Grid.SetColumn(readyPill, 1);
            offerHead.Children.Add(readyPill);
            offerStack.Children.Add(offerHead);

            string[] bulletItems = _library.Updates.OfferNotes
                .SelectMany(r => L10n.IsEnglish && r.NotesEn.Length > 0 ? r.NotesEn : r.NotesZh)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Take(5)
                .ToArray();
            if (bulletItems.Length == 0)
            {
                var fallbackArr = L10n.IsEnglish && activeOffer.Notes.En.Length > 0 ? activeOffer.Notes.En : activeOffer.Notes.Zh;
                bulletItems = fallbackArr.Where(s => !string.IsNullOrWhiteSpace(s)).Take(5).ToArray();
            }
            if (bulletItems.Length > 0)
            {
                var bulletsPanel = new StackPanel { Spacing = 4 };
                foreach (var bLine in bulletItems)
                {
                    bulletsPanel.Children.Add(new TextBlock
                    {
                        Text = "• " + bLine,
                        FontSize = 12,
                        LineHeight = 19,
                        Foreground = ThemeHelper.Brush(p.T1),
                        TextWrapping = TextWrapping.Wrap,
                    });
                }
                offerStack.Children.Add(bulletsPanel);
            }

            var offerActions = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 2, 0, 0) };
            offerActions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            offerActions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            offerActions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            offerActions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            string installLabel = !string.IsNullOrWhiteSpace(_library.Updates.LatestWinInstallerUrl) ? "下载并安装" : "前往下载安装";
            var installNowBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYPrimaryPillButtonStyle"],
                Content = installLabel,
                Height = 28,
                MinHeight = 28,
            };
            installNowBtn.Click += async (_, _) =>
            {
                installNowBtn.IsEnabled = false;
                try
                {
                    await _library.Updates.InstallOfferAsync(RootGrid.XamlRoot, activeOffer);
                    if (_closed) return;
                    if (_activePage == "about") RenderDynamicPage("about");
                    MountIcons();
                }
                finally { if (!_closed) installNowBtn.IsEnabled = true; }
            };
            offerActions.Children.Add(installNowBtn);

            var remindBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
                Content = "稍后提醒",
                Height = 28,
                MinHeight = 28,
            };
            remindBtn.Click += (_, _) =>
            {
                _library.Prefs.UpdateRemindVersion = activeOffer.Version;
                _library.Prefs.UpdateRemindUntilMs = DateTimeOffset.UtcNow.AddDays(3).ToUnixTimeMilliseconds();
                _library.SavePrefs();
                _library.Updates.NotifyStateChanged();
            };
            Grid.SetColumn(remindBtn, 1);
            offerActions.Children.Add(remindBtn);

            var skipVerBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
                Content = "跳过此版本",
                FontSize = 11.5,
                Foreground = ThemeHelper.Brush(p.T3),
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            skipVerBtn.Click += (_, _) =>
            {
                _library.Prefs.UpdateSkippedVersion = activeOffer.Version;
                _library.SavePrefs();
                _library.Updates.NotifyStateChanged();
            };
            Grid.SetColumn(skipVerBtn, 3);
            offerActions.Children.Add(skipVerBtn);
            offerStack.Children.Add(offerActions);

            rightStack.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(12),
                Background = ThemeHelper.Brush(tint.Soft),
                BorderBrush = ThemeHelper.Brush(tint.Edge),
                BorderThickness = new Thickness(1),
                Child = offerStack,
            });
        }

        string FormatLastChecked()
        {
            if (_library.Prefs.UpdateLastCheckedMs is not long ms || ms <= 0) return L10n.T("尚未检查更新");
            long ageMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - ms;
            if (ageMs >= 0 && ageMs < 90_000) return L10n.T("刚刚检查过");
            var dt = DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;
            string when = dt.Date == DateTime.Today ? $"今天 {dt:HH:mm}" : $"{dt:M 月 d 日 HH:mm}";
            return L10n.T($"{when} 检查过");
        }

        bool checkedCurrentBuild = _library.Prefs.UpdateCheckedBuild == UpdateAndFeedbackService.CurrentBuild &&
            _library.Prefs.UpdateLastCheckedMs is > 0;

        string initialStatus;
        Color initialStatusColor;
        if (_aboutCheckingInline)
        {
            initialStatus = L10n.T("正在检查更新…");
            initialStatusColor = p.T2;
        }
        else if (!string.IsNullOrWhiteSpace(_aboutLastInlineCheckError))
        {
            initialStatus = _aboutLastInlineCheckError!;
            initialStatusColor = p.Danger;
        }
        else if (_library.Updates.LatestOffer is { } off)
        {
            initialStatus = L10n.T($"发现新版本 v{off.Version}");
            initialStatusColor = tint.Accent;
        }
        else if (checkedCurrentBuild || _aboutJustCheckedUpToDate)
        {
            initialStatus = $"{L10n.T("已是最新")} · {FormatLastChecked()}";
            initialStatusColor = p.Ok;
        }
        else
        {
            initialStatus = L10n.T("尚未检查更新");
            initialStatusColor = p.T3;
        }

        var statusLine = new TextBlock
        {
            Text = initialStatus,
            FontSize = 12,
            Foreground = ThemeHelper.Brush(initialStatusColor),
            TextWrapping = TextWrapping.Wrap,
        };
        string checkBtnLabel = _aboutCheckingInline
            ? "正在检查…"
            : (!string.IsNullOrWhiteSpace(_aboutLastInlineCheckError) ? "重试" : "检查更新");
        var checkBtn = new Button
        {
            Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
            Content = checkBtnLabel,
            IsEnabled = !_aboutCheckingInline,
        };
        checkBtn.Click += async (_, _) =>
        {
            if (_aboutCheckingInline) return;
            _aboutCheckingInline = true;
            _aboutLastInlineCheckError = null;
            checkBtn.IsEnabled = false;
            checkBtn.Content = L10n.T("正在检查…");
            statusLine.Text = L10n.T("正在检查更新…");
            statusLine.Foreground = ThemeHelper.Brush(p.T2);
            try
            {
                var (hasUpdate, info, _) = await _library.Updates.CheckForUpdatesAsync(manual: true);
                if (_closed) return;
                if (!hasUpdate && info is null)
                {
                    _aboutLastInlineCheckError = L10n.T("无法检查更新，请检查网络连接后重试。");
                    _aboutJustCheckedUpToDate = false;
                }
                else if (!hasUpdate)
                {
                    _aboutLastInlineCheckError = null;
                    _aboutJustCheckedUpToDate = true;
                }
                else
                {
                    _aboutLastInlineCheckError = null;
                    _aboutJustCheckedUpToDate = false;
                    // Manual check explicitly unsuppresses skipped/deferred banner so the user sees the offer
                    _library.Prefs.UpdateSkippedVersion = "";
                    _library.Prefs.UpdateRemindVersion = "";
                    _library.Prefs.UpdateRemindUntilMs = 0;
                    _library.SavePrefs();
                }
            }
            catch
            {
                if (_closed) return;
                _aboutLastInlineCheckError = L10n.T("无法检查更新，请检查网络连接后重试。");
                _aboutJustCheckedUpToDate = false;
            }
            finally
            {
                _aboutCheckingInline = false;
                if (!_closed)
                {
                    MountIcons();
                    if (_activePage == "about") RenderDynamicPage("about");
                }
            }
        };

        Release[] releases = _library.Updates.LoadBundledChangelog();
        int? currentEdition = UpdateAndFeedbackService.ResolveEdition(UpdateAndFeedbackService.CurrentVersion, releases);
        var verRow = new Grid { Padding = new Thickness(16, 14, 16, 14), ColumnSpacing = 10 };
        verRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        verRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var verLeft = new StackPanel { Spacing = 3 };
        var verHead = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        if (currentEdition is int ed)
        {
            verHead.Children.Add(new TextBlock { Text = $"第 {ed} 版", FontFamily = serif, FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
            verHead.Children.Add(new TextBlock { Text = $"v{UpdateAndFeedbackService.CurrentVersion} ({UpdateAndFeedbackService.CurrentBuild})", FontFamily = mono, FontSize = 12, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 2) });
        }
        else
        {
            verHead.Children.Add(new TextBlock { Text = $"v{UpdateAndFeedbackService.CurrentVersion} ({UpdateAndFeedbackService.CurrentBuild})", FontFamily = serif, FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
        }
        verLeft.Children.Add(verHead);
        if (!showActiveOfferCard)
            verLeft.Children.Add(statusLine);
        verRow.Children.Add(verLeft);
        if (!showActiveOfferCard)
        {
            checkBtn.HorizontalAlignment = HorizontalAlignment.Right;
            checkBtn.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(checkBtn, 1);
            verRow.Children.Add(checkBtn);
        }

        // Skipped / Remind-Later quiet update banner with 1-click inline reopen (no modal dialog)
        if (_library.Updates.LatestOffer is { } suppressed && _library.Updates.IsOfferSuppressed)
        {
            bool skipped = suppressed.Version == _library.Prefs.UpdateSkippedVersion;
            var bannerGrid = new Grid { Padding = new Thickness(14, 9, 14, 9), ColumnSpacing = 10 };
            bannerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bannerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            bannerGrid.Children.Add(new TextBlock
            {
                Text = skipped ? $"已跳过新版本 v{suppressed.Version}" : $"已推迟提醒新版本 v{suppressed.Version}",
                FontSize = 12,
                Foreground = ThemeHelper.Brush(p.T2),
                VerticalAlignment = VerticalAlignment.Center,
            });
            var reopenBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
                Content = "重新查看更新",
                Height = 25,
                MinHeight = 25,
                FontSize = 11,
            };
            reopenBtn.Click += (_, _) =>
            {
                _library.Prefs.UpdateSkippedVersion = "";
                _library.Prefs.UpdateRemindVersion = "";
                _library.Prefs.UpdateRemindUntilMs = 0;
                _library.SavePrefs();
                _library.Updates.NotifyStateChanged();
            };
            Grid.SetColumn(reopenBtn, 1);
            bannerGrid.Children.Add(reopenBtn);

            rightStack.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(11),
                Background = ThemeHelper.Brush(p.Inset),
                BorderBrush = ThemeHelper.Brush(p.Line),
                BorderThickness = new Thickness(1),
                Child = bannerGrid,
            });
        }

        Button MakeAboutNavRow(string title, string value, Action onClick)
        {
            var g = new Grid { Padding = new Thickness(16, 10, 16, 10), MinHeight = 40 };
            g.Children.Add(new TextBlock { Text = title, FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1), VerticalAlignment = VerticalAlignment.Center });
            var trail = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            trail.Children.Add(new TextBlock { Text = value, FontSize = 12, Foreground = ThemeHelper.Brush(p.T3) });
            trail.Children.Add(YYIcons.Make("chevron-right", 10, ThemeHelper.Brush(p.T3)));
            g.Children.Add(trail);
            var b = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                UseSystemFocusVisuals = false,
                Content = g,
            };
            AutomationProperties.SetName(b, L10n.T(title));
            b.Click += (_, _) => onClick();
            return b;
        }

        int firstCount = releases.Length > 0 ? Math.Max(1, (releases[0].NotesZh.Length > 0 ? releases[0].NotesZh : releases[0].NotesEn).Length) : 1;
        string logSummary = releases.Length > 0 ? $"{releases[0].Date.Replace('-', '.')} · {firstCount} 项" : "2026.11.23 · 1 项";

        // 更新渠道下拉（正式版 / 测试版，1:1 MacUpdate.swift:275-294）
        var chMenu = new MenuFlyout();
        foreach (var (ck, cl) in new[] { ("stable", "正式版"), ("beta", "测试版") })
        {
            var mi = new MenuFlyoutItem { Text = L10n.T(_library.Prefs.UpdateChannel == ck ? $"✓ {cl}" : cl) };
            string key = ck;
            string lbl = cl;
            mi.Click += (_, _) =>
            {
                string prev = _library.Prefs.UpdateChannel;
                if (prev == key) return;
                string prevLbl = prev == "beta" ? "测试版" : "正式版";
                _library.Prefs.UpdateChannel = key;
                _library.SavePrefs();
                RecordPrefChange(
                    nameof(DevicePrefs.UpdateChannel),
                    "更新渠道",
                    lbl,
                    prevLbl,
                    prev,
                    key,
                    undo: () => { _library.Prefs.UpdateChannel = prev; _library.SavePrefs(); if (_activePage == "about") RenderDynamicPage("about"); },
                    redo: () => { _library.Prefs.UpdateChannel = key; _library.SavePrefs(); if (_activePage == "about") RenderDynamicPage("about"); });
                RenderDynamicPage("about");
            };
            chMenu.Items.Add(mi);
        }
        var chBtn = new Button
        {
            Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
            Content = L10n.T(_library.Prefs.UpdateChannel == "beta" ? "测试版" : "正式版") + " ⇕",
            Flyout = chMenu,
        };

        rightStack.Children.Add(MakeCard(
            verRow,
            MakeAboutNavRow("更新日志", logSummary, async () => await _library.Updates.ShowChangelogSheetAsync(RootGrid.XamlRoot)),
            MakeRow("自动检查更新", "每天启动时检查一次，仅读取版本号，不上传本地数据", MakeSwitch(_library.Prefs.AutoCheckUpdates, v =>
            {
                bool prev = _library.Prefs.AutoCheckUpdates;
                if (prev == v) return;
                _library.Prefs.AutoCheckUpdates = v;
                _library.SavePrefs();
                RecordPrefChange(
                    nameof(DevicePrefs.AutoCheckUpdates),
                    "自动检查更新",
                    v ? "开" : "关",
                    prev ? "开" : "关",
                    prev,
                    v,
                    undo: () => { _library.Prefs.AutoCheckUpdates = prev; _library.SavePrefs(); if (_activePage == "about") RenderDynamicPage("about"); },
                    redo: () => { _library.Prefs.AutoCheckUpdates = v; _library.SavePrefs(); if (_activePage == "about") RenderDynamicPage("about"); });
            })),
            MakeRow("更新渠道", "测试版先拿到新功能；换回正式版后随下个正式版恢复", chBtn)));

        string repoUrl = _library.Updates.GetPreferredRepoUrl();
        string issuesUrl = preferCn ? $"{repoUrl}/-/issues" : $"{repoUrl}/issues";
        string issuesBtnLabel = preferCn ? "CNB ↗" : "GitHub ↗";
        string srcDisplay = preferCn ? "cnb.cool/inokoru/yuyue ↗" : "github.com/inokoru/yuyue ↗";

        var feedbackRight = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var ghIssuesBtn = new Button { Style = (Style)Application.Current.Resources["YYIconButtonStyle"], Content = issuesBtnLabel, FontSize = 12, Foreground = ThemeHelper.Brush(p.T2) };
        ghIssuesBtn.Click += (_, _) => _ = Launcher.LaunchUriAsync(new Uri(issuesUrl));
        var writeFbBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "写反馈…", Height = 26, MinHeight = 26 };
        writeFbBtn.Click += async (_, _) => await _library.Updates.ShowFeedbackComposerAsync(this, RootGrid.XamlRoot);
        feedbackRight.Children.Add(ghIssuesBtn);
        feedbackRight.Children.Add(writeFbBtn);

        var srcBtn = new Button { Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"], Content = srcDisplay, FontSize = 12.5, Foreground = ThemeHelper.Brush(p.T1) };
        srcBtn.Click += (_, _) => _ = Launcher.LaunchUriAsync(new Uri(repoUrl));

        rightStack.Children.Add(MakeCard(
            MakeRow("问题与建议", "可附截图，无需注册账号", feedbackRight),
            MakeRow("匿名使用统计", "仅记录功能使用形态，不关联身份，绝不收集书名、笔记或搜索词", MakeSwitch(_library.Prefs.AnalyticsEnabled, v =>
            {
                bool prev = _library.Prefs.AnalyticsEnabled;
                if (prev == v) return;
                _library.Analytics.SetEnabled(v);
                RecordPrefChange(
                    nameof(DevicePrefs.AnalyticsEnabled),
                    "匿名使用统计",
                    v ? "开" : "关",
                    prev ? "开" : "关",
                    prev,
                    v,
                    undo: () => { _library.Analytics.SetEnabled(prev); if (_activePage == "about") RenderDynamicPage("about"); },
                    redo: () => { _library.Analytics.SetEnabled(v); if (_activePage == "about") RenderDynamicPage("about"); });
            })),
            MakeAboutNavRow("隐私说明", "书库仅存本机 · 可关匿名统计", async () => await UpdateAndFeedbackService.ShowPrivacySheetAsync(RootGrid.XamlRoot)),
            MakeAboutNavRow("开源许可与致谢", "GPL-3.0 与组件清单", async () => await UpdateAndFeedbackService.ShowLicensesSheetAsync(RootGrid.XamlRoot)),
            MakeRow("源代码", null, srcBtn, compact: true)));

        Grid.SetColumn(rightStack, 1);
        bookplate.Children.Add(rightStack);
        DynamicPageHost.Children.Add(bookplate);
    }

    private Task ShowInfoDialogAsync(string title, string message) =>
        YYChrome.ShowNoticeAsync(
            RootGrid.XamlRoot,
            IsDark,
            L10n.T(title),
            message,
            okText: L10n.T("好"),
            tint: _library.Prefs.Tint);

    private static long DirSize(string dir)
    {
        if (!Directory.Exists(dir)) return 0;
        long total = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(f).Length; } catch { }
            }
        }
        catch { }
        return total;
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):0.00} GB",
        >= 1024L * 1024 => $"{bytes / (1024.0 * 1024):0.0} MB",
        >= 1024L => $"{bytes / 1024.0:0.0} KB",
        _ => $"{bytes} B",
    };

    // ---------- Appearance Page Event Handlers ----------

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string page) NavigateTo(page);
    }

    private void ThemeMode_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string mode)
        {
            _hoveredThemeMode = mode;
            RefreshThemeModePreviews();
        }
    }

    private void ThemeMode_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string mode && _hoveredThemeMode == mode)
        {
            _hoveredThemeMode = null;
            RefreshThemeModePreviews();
        }
    }

    private void ThemeMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string mode) return;
        string prev = _library.Prefs.ThemeMode;
        if (prev == mode) return;
        string LabelOf(string m) => m switch { "light" => "浅色", "dark" => "深色", _ => "跟随系统" };
        _library.Prefs.ThemeMode = mode;
        _library.SavePrefs();
        RecordPrefChange(
            nameof(DevicePrefs.ThemeMode),
            "界面外观",
            LabelOf(mode),
            LabelOf(prev),
            prev,
            mode,
            undo: () => { _library.Prefs.ThemeMode = prev; _library.SavePrefs(); App.CurrentApp.BroadcastAppearanceChanged(); },
            redo: () => { _library.Prefs.ThemeMode = mode; _library.SavePrefs(); App.CurrentApp.BroadcastAppearanceChanged(); });
        App.CurrentApp.BroadcastAppearanceChanged();
    }

    private void RenderAppIconChoices()
    {
        if (AppIconsGridHost is null || ActiveAppIconTitleText is null) return;
        bool dark = IsDark;
        var p = CurrentPalette;
        var tint = CurrentTint;
        string activeId = ThemeHelper.NormalizeAppIconId(_library.Prefs.AppIconId);
        ActiveAppIconTitleText.Text = L10n.T(ThemeHelper.GetAppIconTitle(activeId));

        AppIconsGridHost.Children.Clear();
        AppIconsGridHost.ColumnDefinitions.Clear();
        for (int i = 0; i < ThemeHelper.AppIcons.Length; i++)
            AppIconsGridHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        for (int i = 0; i < ThemeHelper.AppIcons.Length; i++)
        {
            var (id, title) = ThemeHelper.AppIcons[i];
            bool on = id == activeId;
            string pngPath = ThemeHelper.ResolveAppIconAssetPath(id, dark, ico: false);

            var img = new Image
            {
                Width = 52,
                Height = 52,
                Stretch = Stretch.Uniform,
            };
            if (File.Exists(pngPath))
            {
                var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage { DecodePixelWidth = 104 };
                bmp.UriSource = new Uri(pngPath);
                img.Source = bmp;
            }

            var ring = new Border
            {
                Padding = new Thickness(3),
                CornerRadius = new CornerRadius(16.5),
                BorderThickness = new Thickness(on ? 1.75 : 1.0),
                BorderBrush = on ? ThemeHelper.Brush(tint.Accent) : new SolidColorBrush(Colors.Transparent),
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = img,
            };

            var lbl = new TextBlock
            {
                Text = L10n.T(title),
                FontSize = 11.5,
                FontWeight = on ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                Foreground = ThemeHelper.Brush(on ? p.T1 : p.T2),
                HorizontalAlignment = HorizontalAlignment.Center,
                MaxLines = 1,
            };

            var tileStack = new StackPanel
            {
                Spacing = 6,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children = { ring, lbl },
            };

            var btn = new Button
            {
                Tag = id,
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0, 2, 0, 2),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                UseSystemFocusVisuals = false,
                Content = tileStack,
            };
            AutomationProperties.SetName(btn, $"{L10n.T("应用图标")}：{L10n.T(title)}");
            AutomationProperties.SetAutomationId(btn, $"appearance.icon.{id}");
            ToolTipService.SetToolTip(btn, L10n.T(on ? "当前正在使用的应用图标" : "轻点切换应用图标"));

            btn.PointerEntered += (_, _) =>
            {
                if (ThemeHelper.NormalizeAppIconId(_library.Prefs.AppIconId) != id)
                {
                    ring.BorderBrush = ThemeHelper.Brush(p.Line);
                    lbl.Foreground = ThemeHelper.Brush(p.T1);
                }
            };
            btn.PointerExited += (_, _) =>
            {
                if (ThemeHelper.NormalizeAppIconId(_library.Prefs.AppIconId) != id)
                {
                    ring.BorderBrush = new SolidColorBrush(Colors.Transparent);
                    lbl.Foreground = ThemeHelper.Brush(p.T2);
                }
            };
            btn.Click += (_, _) =>
            {
                string prev = ThemeHelper.NormalizeAppIconId(_library.Prefs.AppIconId);
                if (prev == id) return;
                _library.Prefs.AppIconId = id;
                _library.SavePrefs();
                RecordPrefChange(
                    nameof(DevicePrefs.AppIconId),
                    "应用图标",
                    ThemeHelper.GetAppIconTitle(id),
                    ThemeHelper.GetAppIconTitle(prev),
                    prev,
                    id,
                    undo: () => { _library.Prefs.AppIconId = prev; _library.SavePrefs(); App.CurrentApp.BroadcastAppearanceChanged(); },
                    redo: () => { _library.Prefs.AppIconId = id; _library.SavePrefs(); App.CurrentApp.BroadcastAppearanceChanged(); });
                App.CurrentApp.BroadcastAppearanceChanged();
            };

            Grid.SetColumn(btn, i);
            AppIconsGridHost.Children.Add(btn);
        }
    }

    private void TintDot_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string tintId) return;
        string prev = _library.Prefs.TintId;
        if (prev == tintId) return;
        string NameOf(string id) => YYTints.All.FirstOrDefault(t => t.Id == id)?.Name ?? id;
        _library.Prefs.TintId = tintId;
        _library.SavePrefs();
        RecordPrefChange(
            nameof(DevicePrefs.TintId),
            "主题色",
            NameOf(tintId),
            NameOf(prev),
            prev,
            tintId,
            undo: () => { _library.Prefs.TintId = prev; _library.SavePrefs(); App.CurrentApp.BroadcastAppearanceChanged(); },
            redo: () => { _library.Prefs.TintId = tintId; _library.SavePrefs(); App.CurrentApp.BroadcastAppearanceChanged(); });
        App.CurrentApp.BroadcastAppearanceChanged();
    }

    private void CoverSizeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_initializing) return;
        double prev = _library.Prefs.CoverWidth;
        double next = Math.Clamp(Math.Round(e.NewValue), 80, 130);
        if (Math.Abs(prev - next) < 0.5) return;
        _library.Prefs.CoverWidth = next;
        _library.SavePrefs();
        RecordPrefChange(
            nameof(DevicePrefs.CoverWidth),
            "封面大小",
            $"{(int)next}",
            $"{(int)prev}",
            prev,
            next,
            undo: () => { _library.Prefs.CoverWidth = prev; _library.SavePrefs(); App.CurrentApp.BroadcastAppearanceChanged(); },
            redo: () => { _library.Prefs.CoverWidth = next; _library.SavePrefs(); App.CurrentApp.BroadcastAppearanceChanged(); });
        App.CurrentApp.BroadcastAppearanceChanged();
    }

    private void ShowProgressToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        bool prev = _library.Prefs.ShowProgress;
        bool on = ShowProgressToggle.IsOn;
        if (prev == on) return;
        _library.Prefs.ShowProgress = on;
        _library.SavePrefs();
        RecordPrefChange(
            nameof(DevicePrefs.ShowProgress),
            "封面下显示进度",
            on ? "开" : "关",
            prev ? "开" : "关",
            prev,
            on,
            undo: () => { _library.Prefs.ShowProgress = prev; _library.SavePrefs(); App.CurrentApp.BroadcastAppearanceChanged(); },
            redo: () => { _library.Prefs.ShowProgress = on; _library.SavePrefs(); App.CurrentApp.BroadcastAppearanceChanged(); });
        App.CurrentApp.BroadcastAppearanceChanged();
    }
}
