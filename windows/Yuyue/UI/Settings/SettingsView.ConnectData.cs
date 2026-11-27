using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Yuyue.Core;
using Yuyue.Design;
using Yuyue.UI.Controls;

namespace Yuyue.UI.Settings;

public sealed partial class SettingsView
{
    private List<string> CollectAddedCompanionProviderIds()
    {
        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddIfValid(string? id)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            string clean = id.Trim();
            if (string.Equals(clean, "custom", StringComparison.OrdinalIgnoreCase)) return;
            if (seen.Add(clean)) list.Add(clean);
        }

        string activeId = string.IsNullOrWhiteSpace(_library.Prefs.AiProvider)
            ? CompanionClient.Presets[0].Id
            : _library.Prefs.AiProvider.Trim();

        foreach (var id in _library.Prefs.ConfiguredAiProviders) AddIfValid(id);
        AddIfValid(activeId);

        foreach (var pr in CompanionClient.Presets)
        {
            if (string.Equals(pr.Id, "custom", StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrWhiteSpace(_library.Secrets.Get($"ai.key.{pr.Id}")))
                AddIfValid(pr.Id);
        }
        foreach (var kv in _library.Prefs.AiCustomProviderNames)
            AddIfValid(kv.Key);

        if (list.Count == 0)
            list.Add("deepseek");

        // Keep active provider easy to spot (first when added list is short, otherwise preserve order)
        return list;
    }

    private string ResolveCompanionProviderName(string providerId)
    {
        if (CompanionClient.FindPreset(providerId) is { } pr) return pr.Name;
        if (_library.Prefs.AiCustomProviderNames.TryGetValue(providerId, out var cn) && !string.IsNullOrWhiteSpace(cn))
            return cn.Trim();
        return providerId;
    }

    private void SwitchCompanionProvider(string providerId)
    {
        var preset = CompanionClient.FindPreset(providerId);
        if (!_library.Prefs.ConfiguredAiProviders.Contains(providerId, StringComparer.OrdinalIgnoreCase))
            _library.Prefs.ConfiguredAiProviders.Add(providerId);

        _library.Prefs.AiProvider = providerId;
        _library.Prefs.AiBaseUrl = _library.Prefs.AiProviderUrls.TryGetValue(providerId, out var savedUrl) && !string.IsNullOrWhiteSpace(savedUrl)
            ? savedUrl
            : (preset?.BaseUrl ?? "");
        _library.Prefs.AiModel = _library.Prefs.AiProviderModels.TryGetValue(providerId, out var savedModel) && !string.IsNullOrWhiteSpace(savedModel)
            ? savedModel
            : (preset?.DefaultModel ?? "");
        _library.SavePrefs();
    }

    private async Task AddCustomCompanionProviderAsync(bool dark)
    {
        if (RootGrid.XamlRoot is not { } xr) return;
        string? name = await YYChrome.ShowPromptAsync(
            xr,
            dark,
            "添加自定义服务商",
            placeholder: "如：公司自建 vLLM / OneAPI / 中转站",
            confirmText: "添加",
            tint: _library.Prefs.Tint);
        if (_disposed || string.IsNullOrWhiteSpace(name)) return;
        string newId = $"custom_{Guid.NewGuid():N}"[..16];
        _library.Prefs.AiCustomProviderNames[newId] = name.Trim();
        if (!_library.Prefs.ConfiguredAiProviders.Contains(newId, StringComparer.OrdinalIgnoreCase))
            _library.Prefs.ConfiguredAiProviders.Add(newId);
        _library.Prefs.AiProvider = newId;
        _library.Prefs.AiBaseUrl = "https://api.openai.com/v1";
        _library.Prefs.AiModel = "";
        _library.Prefs.AiProviderUrls[newId] = _library.Prefs.AiBaseUrl;
        _library.SavePrefs();
        ShowSettingsSavedToast($"已添加服务商“{name.Trim()}”");
        RenderActivePage();
    }

    private void BuildCompanionPage(bool dark)
    {
        var p = ThemeHelper.Palette(dark);
        var tc = ThemeHelper.TintColors(_library.Prefs.Tint, dark);

        var addedIds = CollectAddedCompanionProviderIds();
        string curPid = string.IsNullOrWhiteSpace(_library.Prefs.AiProvider) ? addedIds[0] : _library.Prefs.AiProvider;
        if (!addedIds.Contains(curPid, StringComparer.OrdinalIgnoreCase))
            curPid = addedIds[0];

        PageBodyStack.Children.Add(MakeSectionHeader($"{L10n.T("已添加的服务商")} ({addedIds.Count})", dark, first: true));

        foreach (var pid in addedIds)
        {
            string pidCap = pid;
            var preset = CompanionClient.FindPreset(pidCap);
            bool isCustom = preset is null;
            bool isActive = string.Equals(pidCap, curPid, StringComparison.OrdinalIgnoreCase);
            string displayName = ResolveCompanionProviderName(pidCap);
            string savedModel = isActive
                ? _library.Prefs.AiModel
                : (_library.Prefs.AiProviderModels.TryGetValue(pidCap, out var pm) && !string.IsNullOrWhiteSpace(pm) ? pm : (preset?.DefaultModel ?? ""));
            bool hasKey = !string.IsNullOrWhiteSpace(_library.Secrets.Get($"ai.key.{pidCap}"))
                || (isActive && !string.IsNullOrWhiteSpace(_library.Secrets.Get("ai.apiKey")));
            bool isLocal = preset is { NeedsKey: false };

            string statusPart = isLocal
                ? L10n.T("本机 / 局域网免 Key")
                : hasKey
                    ? L10n.T("Key 已加密保存")
                    : L10n.T("未填 Key");
            string cardDesc = string.IsNullOrWhiteSpace(savedModel)
                ? statusPart
                : $"{savedModel} · {statusPart}";

            var rowActs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            if (isActive)
            {
                rowActs.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(4),
                    Background = ThemeHelper.Brush(tc.Soft),
                    BorderBrush = ThemeHelper.Brush(tc.Edge),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(8, 2, 8, 2),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = L10n.T("当前使用"),
                        FontSize = 11,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = ThemeHelper.Brush(tc.Accent),
                    },
                });
            }
            else
            {
                var useBtn = new Button
                {
                    Style = (Style)Application.Current.Resources["YYFluentButtonStyle"],
                    Height = 28,
                    MinHeight = 28,
                    Content = L10n.T("使用"),
                };
                useBtn.Click += (_, _) =>
                {
                    SwitchCompanionProvider(pidCap);
                    RenderActivePage();
                };
                rowActs.Children.Add(useBtn);
            }

            if (isCustom)
            {
                var renameBtn = new Button
                {
                    Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
                    Height = 28,
                    MinHeight = 28,
                    Content = L10n.T("改名"),
                };
                renameBtn.Click += async (_, _) =>
                {
                    if (RootGrid.XamlRoot is not { } xr) return;
                    string? nn = await YYChrome.ShowPromptAsync(xr, dark, "重命名服务商", initialText: displayName, confirmText: "保存", tint: _library.Prefs.Tint);
                    if (_disposed || string.IsNullOrWhiteSpace(nn)) return;
                    _library.Prefs.AiCustomProviderNames[pidCap] = nn.Trim();
                    _library.SavePrefs();
                    RenderActivePage();
                };
                rowActs.Children.Add(renameBtn);
            }

            if (addedIds.Count > 1 || isCustom)
            {
                var rmBtn = new Button
                {
                    Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
                    Height = 28,
                    MinHeight = 28,
                    Content = L10n.T("移除"),
                    Foreground = ThemeHelper.Brush(p.Danger),
                };
                rmBtn.Click += (_, _) =>
                {
                    _library.Prefs.ConfiguredAiProviders.RemoveAll(x => string.Equals(x, pidCap, StringComparison.OrdinalIgnoreCase));
                    _library.Secrets.Set($"ai.key.{pidCap}", "");
                    if (isActive && !string.IsNullOrEmpty(_library.Secrets.Get("ai.apiKey")))
                        _library.Secrets.Set("ai.apiKey", "");
                    if (isCustom)
                    {
                        _library.Prefs.AiCustomProviderNames.Remove(pidCap);
                        _library.Prefs.AiProviderUrls.Remove(pidCap);
                        _library.Prefs.AiProviderModels.Remove(pidCap);
                        _library.Prefs.AiProviderFastModels.Remove(pidCap);
                    }
                    if (isActive)
                    {
                        string nextId = addedIds.FirstOrDefault(x => !string.Equals(x, pidCap, StringComparison.OrdinalIgnoreCase)) ?? "deepseek";
                        SwitchCompanionProvider(nextId);
                    }
                    else
                    {
                        _library.SavePrefs();
                    }
                    ShowSettingsSavedToast($"已移除“{displayName}”");
                    RenderActivePage();
                };
                rowActs.Children.Add(rmBtn);
            }

            PageBodyStack.Children.Add(new FluentSettingsCard(
                isCustom ? $"{displayName} ({L10n.T("自定义")})" : displayName,
                cardDesc,
                "sparkle",
                rowActs,
                onClick: isActive ? null : () =>
                {
                    SwitchCompanionProvider(pidCap);
                    RenderActivePage();
                },
                dark: dark,
                customIcon: ThemeHelper.CreateBrandLogoImage(pidCap, dark, 20)));
        }

        // Compact catalog trigger card (scales cleanly to 50+ providers)
        int catalogPresetCount = CompanionClient.Presets.Count(x => !string.Equals(x.Id, "custom", StringComparison.OrdinalIgnoreCase));
        var catalogHint = L10n.Skip(new TextBlock
        {
            Text = $"{catalogPresetCount}+",
            FontSize = 11.5,
            Foreground = ThemeHelper.Brush(p.T3),
            VerticalAlignment = VerticalAlignment.Center,
        });
        PageBodyStack.Children.Add(new FluentSettingsCard(
            "添加服务商…",
            "按分类搜索 OpenRouter、Claude、OpenAI、Gemini、DeepSeek、通义千问、Kimi、Ollama 或自定义接口",
            "plus",
            catalogHint,
            onClick: () => _ = ShowCompanionProviderCatalogDialogAsync(dark),
            dark: dark));

        // Configuration section for the currently active provider
        var curPreset = CompanionClient.FindPreset(curPid);
        string curDisplayName = ResolveCompanionProviderName(curPid);
        PageBodyStack.Children.Add(MakeSectionHeader($"{L10n.T("当前配置")} · {curDisplayName}", dark));

        // OpenRouter 1-click OAuth Sign-in card
        if (string.Equals(curPid, "openrouter", StringComparison.OrdinalIgnoreCase))
        {
            var orActs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            var orCancelBtn = new Button { Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"], Content = L10n.T("取消"), Visibility = Visibility.Collapsed };
            orCancelBtn.Click += (_, _) => { try { _openRouterAuthCts?.Cancel(); } catch { } };
            var orSignBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentAccentButtonStyle"], Content = L10n.T("用 OpenRouter 账号登录") };
            orSignBtn.Click += async (_, _) =>
            {
                try { _openRouterAuthCts?.Cancel(); _openRouterAuthCts?.Dispose(); } catch { }
                var cts = new CancellationTokenSource();
                _openRouterAuthCts = cts;
                orSignBtn.IsEnabled = false;
                orSignBtn.Content = L10n.T("等待授权…");
                orCancelBtn.Visibility = Visibility.Visible;
                try
                {
                    string gotKey = await CompanionClient.SignInWithOpenRouterAsync(cts.Token);
                    if (_disposed) return;
                    _library.Secrets.Set("ai.key.openrouter", gotKey);
                    _library.Secrets.Set("ai.apiKey", "");
                    if (!_library.Prefs.ConfiguredAiProviders.Contains("openrouter", StringComparer.OrdinalIgnoreCase))
                        _library.Prefs.ConfiguredAiProviders.Insert(0, "openrouter");
                    _library.SavePrefs();
                    _ownerWindow.ShowToast("OpenRouter 账号授权成功，已加密保存 API Key");
                }
                catch (OperationCanceledException)
                {
                    if (!_disposed) _ownerWindow.ShowToast("已取消 OpenRouter 授权");
                }
                catch (Exception ex)
                {
                    if (!_disposed) _ownerWindow.ShowToast($"OpenRouter 授权失败：{ex.Message}");
                }
                finally
                {
                    if (ReferenceEquals(_openRouterAuthCts, cts))
                    {
                        try { cts.Dispose(); } catch { }
                        _openRouterAuthCts = null;
                    }
                    orSignBtn.IsEnabled = true;
                    orSignBtn.Content = L10n.T("用 OpenRouter 账号登录");
                    orCancelBtn.Visibility = Visibility.Collapsed;
                    if (!_disposed && _activePage == "companion") RenderActivePage();
                }
            };
            orActs.Children.Add(orCancelBtn);
            orActs.Children.Add(orSignBtn);
            PageBodyStack.Children.Add(new FluentSettingsCard(
                "用 OpenRouter 账号登录",
                "登录就能用，几百个模型一个账号",
                "globe",
                orActs,
                dark: dark,
                customIcon: ThemeHelper.CreateBrandLogoImage("openrouter", dark, 20)));
        }

        var keyRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        var keyBox = new PasswordBox
        {
            Style = (Style)Application.Current.Resources["YYCompactPasswordBoxStyle"],
            Width = !string.IsNullOrWhiteSpace(curPreset?.KeyUrl) ? 210 : 260,
            Password = _library.Secrets.Get($"ai.key.{curPid}") ?? "",
            PlaceholderText = string.IsNullOrWhiteSpace(curPreset?.KeyHint) ? "sk-…" : curPreset.KeyHint,
        };
        keyBox.PasswordChanged += (_, _) =>
        {
            _library.Secrets.Set($"ai.key.{curPid}", keyBox.Password);
            if (!string.IsNullOrEmpty(_library.Secrets.Get("ai.apiKey")))
                _library.Secrets.Set("ai.apiKey", "");
            if (!string.IsNullOrWhiteSpace(keyBox.Password) && !_library.Prefs.ConfiguredAiProviders.Contains(curPid, StringComparer.OrdinalIgnoreCase))
            {
                _library.Prefs.ConfiguredAiProviders.Add(curPid);
                _library.SavePrefs();
            }
        };
        keyRow.Children.Add(keyBox);
        if (!string.IsNullOrWhiteSpace(curPreset?.KeyUrl))
        {
            string keyUrl = curPreset.KeyUrl;
            var getKeyBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
                Height = 28,
                MinHeight = 28,
                Content = L10n.T("获取 ↗"),
            };
            ToolTipService.SetToolTip(getKeyBtn, L10n.T("在浏览器打开服务商控制台获取 API Key"));
            getKeyBtn.Click += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo(keyUrl) { UseShellExecute = true }); } catch { }
            };
            keyRow.Children.Add(getKeyBtn);
        }
        PageBodyStack.Children.Add(new FluentSettingsCard(
            "API Key",
            curPreset is { NeedsKey: false } ? "本机服务通常可留空；若在局域网启用鉴权再填写" : "使用 Windows DPAPI 本地加密保存，从不上传服务器",
            "lock",
            keyRow,
            dark: dark));

        var urlBox = new TextBox { Style = (Style)Application.Current.Resources["YYSingleLineTextBoxStyle"], Width = 260, Text = _library.Prefs.AiBaseUrl };
        urlBox.TextChanged += (_, _) =>
        {
            _library.Prefs.AiBaseUrl = urlBox.Text ?? "";
            _library.Prefs.AiProviderUrls[curPid] = _library.Prefs.AiBaseUrl;
            _library.SavePrefs();
        };
        PageBodyStack.Children.Add(new FluentSettingsCard("接口地址", "兼容 OpenAI / Anthropic / Gemini 协议", "globe", urlBox, dark: dark));

        var modelRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        var modelBox = new TextBox { Style = (Style)Application.Current.Resources["YYSingleLineTextBoxStyle"], Width = 200, Text = _library.Prefs.AiModel };
        modelBox.TextChanged += (_, _) =>
        {
            _library.Prefs.AiModel = modelBox.Text ?? "";
            _library.Prefs.AiProviderModels[curPid] = _library.Prefs.AiModel;
            _library.SavePrefs();
        };
        var fetchModelsBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentButtonStyle"], Content = L10n.T("选择模型…") };
        fetchModelsBtn.Click += async (_, _) =>
        {
            await ShowCompanionModelPickerDialogAsync(curPid, urlBox.Text, keyBox.Password, modelBox, dark);
        };
        modelRow.Children.Add(modelBox);
        modelRow.Children.Add(fetchModelsBtn);
        PageBodyStack.Children.Add(new FluentSettingsCard("模型", "如 deepseek-chat / claude-sonnet-4-5 / qwen2.5:7b", "bubble-lines", modelRow, dark: dark));

        string defaultFast = curPreset?.DefaultFastModel ?? "";
        var fastModelBox = new TextBox
        {
            Style = (Style)Application.Current.Resources["YYSingleLineTextBoxStyle"],
            Width = 260,
            Text = _library.Prefs.AiProviderFastModels.GetValueOrDefault(curPid, ""),
            PlaceholderText = string.IsNullOrWhiteSpace(defaultFast) ? L10n.T("留空则自动跟随主力模型") : defaultFast,
        };
        fastModelBox.TextChanged += (_, _) =>
        {
            string val = (fastModelBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(val)) _library.Prefs.AiProviderFastModels.Remove(curPid);
            else _library.Prefs.AiProviderFastModels[curPid] = val;
            _library.SavePrefs();
        };
        PageBodyStack.Children.Add(new FluentSettingsCard("轻量模型", "用于前情提要、角色识别与整理错字，更快更省费", "sparkle", fastModelBox, dark: dark));

        var testBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentAccentButtonStyle"], Content = L10n.T("检查连接") };
        testBtn.Click += async (_, _) =>
        {
            testBtn.IsEnabled = false;
            try
            {
                var (ok, msg) = await _library.Companion.TestConnectionAsync();
                if (!_disposed)
                    _ownerWindow.ShowToast(ok ? $"连接成功：{msg}" : $"连接失败：{msg}");
            }
            finally
            {
                testBtn.IsEnabled = true;
            }
        };
        PageBodyStack.Children.Add(new FluentSettingsCard("检查连接", "发一条简短请求，确认密钥和模型可用", "check-circle", testBtn, dark: dark));

        // 常用提问 (Custom quick prompts, max 8, 1:1 macOS CompanionQuickPromptsSection)
        var qpHeadRow = new Grid { Margin = new Thickness(2, 12, 0, 2) };
        qpHeadRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        qpHeadRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        qpHeadRow.Children.Add(new TextBlock
        {
            Text = L10n.T("常用提问"),
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeHelper.Brush(p.T2),
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (!CompanionPromptsHelper.IsDefault(_library.Prefs))
        {
            var resetQpBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
                Content = L10n.T("恢复默认"),
                FontSize = 11.5,
                Foreground = ThemeHelper.Brush(p.T3),
                MinHeight = 22,
                Padding = new Thickness(6, 0, 6, 0),
            };
            resetQpBtn.Click += (_, _) =>
            {
                CompanionPromptsHelper.ResetDefaults(_library.Prefs);
                _library.SavePrefs();
                ShowSettingsSavedToast("已恢复默认常用提问");
                RenderActivePage();
            };
            Grid.SetColumn(resetQpBtn, 1);
            qpHeadRow.Children.Add(resetQpBtn);
        }
        PageBodyStack.Children.Add(qpHeadRow);

        var qpItems = CompanionPromptsHelper.GetItems(_library.Prefs);
        foreach (var qp in qpItems)
        {
            var qpCap = qp;
            var qpActs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            var cmdBadge = new Border
            {
                CornerRadius = new CornerRadius(4),
                Background = ThemeHelper.Brush(tc.Soft),
                Padding = new Thickness(7, 2, 7, 2),
                Margin = new Thickness(0, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = L10n.Skip(new TextBlock
                {
                    Text = $"/{qpCap.Command}",
                    FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = ThemeHelper.Brush(tc.Accent),
                }),
            };
            var editQpBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
                Content = L10n.T("编辑"),
                FontSize = 11.5,
                MinHeight = 24,
            };
            editQpBtn.Click += async (_, _) => await ShowCompanionPromptEditorAsync(qpCap, dark);
            var delQpBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
                Content = L10n.T("删除"),
                FontSize = 11.5,
                MinHeight = 24,
                Foreground = ThemeHelper.Brush(p.Danger),
            };
            delQpBtn.Click += (_, _) =>
            {
                CompanionPromptsHelper.Remove(_library.Prefs, qpCap.Id);
                _library.SavePrefs();
                ShowSettingsSavedToast("已删除常用提问");
                RenderActivePage();
            };
            qpActs.Children.Add(cmdBadge);
            qpActs.Children.Add(editQpBtn);
            qpActs.Children.Add(delQpBtn);

            PageBodyStack.Children.Add(new FluentSettingsCard(
                qpCap.Prompt,
                null,
                "bubble-lines",
                qpActs,
                dark: dark));
        }

        if (qpItems.Count < CompanionPromptsHelper.MaxCount)
        {
            var addQpBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYFluentButtonStyle"],
                Content = L10n.T("添加…"),
            };
            addQpBtn.Click += async (_, _) => await ShowCompanionPromptEditorAsync(null, dark);
            PageBodyStack.Children.Add(new FluentSettingsCard(
                "添加常用提问",
                $"最多 {CompanionPromptsHelper.MaxCount} 条 · 同时出现在书伴建议与 / 快捷指令菜单中",
                "plus",
                addQpBtn,
                dark: dark));
        }

        // Cost control & usage statistics
        PageBodyStack.Children.Add(MakeSectionHeader("省费与用量", dark));

        var tierSw = new ToggleSwitch { IsOn = _library.Prefs.AiTierRoutingEnabled };
        tierSw.Toggled += (_, _) => { _library.Prefs.AiTierRoutingEnabled = tierSw.IsOn; _library.SavePrefs(); };
        PageBodyStack.Children.Add(new FluentSettingsCard("批量任务自动用轻量模型", "认人、整理错字和提要优先用轻量模型并启用缓存", "sliders", tierSw, dark: dark));

        var llmCapSeg = YYChrome.MakeSegmentedBar(
            [("0", "不限"), ("200000", "20 万"), ("500000", "50 万"), ("1000000", "100 万")],
            _library.Prefs.AiDailyLlmTokenCap.ToString(),
            s =>
            {
                _library.Prefs.AiDailyLlmTokenCap = long.TryParse(s, out long v) ? v : 0;
                _library.SavePrefs();
                RenderActivePage();
            },
            dark,
            fontSize: 11.5,
            height: 28);
        PageBodyStack.Children.Add(new FluentSettingsCard("每日用量上限", "达到后自动暂停后台批量任务", "lock", llmCapSeg, dark: dark));

        var today = _library.Usage.GetToday();
        var snap30 = _library.Usage.Snapshot30Days();
        PageBodyStack.Children.Add(new FluentSettingsCard(
            "Token 用量",
            $"今日 {today.Requests} 次请求（{CompanionUsageLedger.FormatCount(today.TotalTokens)} tokens）· 近 30 天累计 {CompanionUsageLedger.FormatCount(snap30.TotalTokens)} tokens",
            "clock",
            dark: dark));
    }

    private async Task ShowCompanionProviderCatalogDialogAsync(bool dark)
    {
        if (RootGrid.XamlRoot is not { } xr) return;
        var p = ThemeHelper.Palette(dark);
        var tc = ThemeHelper.TintColors(_library.Prefs.Tint, dark);
        var addedSet = new HashSet<string>(CollectAddedCompanionProviderIds(), StringComparer.OrdinalIgnoreCase);
        var liveLocals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        ContentDialog? dlgRef = null;
        bool dialogOpen = true;
        bool openCustomAfter = false;

        var searchBox = YYChrome.MakeSearchBox("搜索服务商，如 千问、Kimi、Claude、DeepSeek、Ollama…");
        var catalogStack = new StackPanel { Spacing = 8, Padding = new Thickness(0, 2, 10, 4) };

        void ChoosePreset(AiProviderPreset preset)
        {
            SwitchCompanionProvider(preset.Id);
            dialogOpen = false;
            dlgRef?.Hide();
            ShowSettingsSavedToast($"已切换至 {preset.Name}");
            RenderActivePage();
        }

        bool Matches(AiProviderPreset pr, string[] terms)
        {
            if (terms.Length == 0) return true;
            string hay = $"{pr.Name} {pr.Id} {pr.Aliases} {pr.Blurb ?? ""}";
            return terms.All(t => hay.Contains(t, StringComparison.OrdinalIgnoreCase));
        }

        Button MakeTile(AiProviderPreset pr)
        {
            var prCap = pr;
            bool isAdded = addedSet.Contains(prCap.Id);
            bool isLive = liveLocals.Contains(prCap.Id);

            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            FrameworkElement icon = (FrameworkElement?)ThemeHelper.CreateBrandLogoImage(prCap.Id, dark, 18)
                ?? YYIcons.Make(prCap.Group == "local" ? "server" : "sparkle", 14, ThemeHelper.Brush(p.T2));
            Grid.SetColumn(icon, 0);
            row.Children.Add(icon);

            var nameStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            nameStack.Children.Add(L10n.Skip(new TextBlock
            {
                Text = prCap.Name,
                FontSize = 12.5,
                FontWeight = isAdded ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = ThemeHelper.Brush(p.T1),
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
            }));
            if (isLive)
            {
                nameStack.Children.Add(new TextBlock
                {
                    Text = L10n.T("运行中"),
                    FontSize = 10,
                    Foreground = ThemeHelper.Brush(p.Ok),
                });
            }
            Grid.SetColumn(nameStack, 1);
            row.Children.Add(nameStack);

            if (isAdded || isLive)
            {
                var dot = new Microsoft.UI.Xaml.Shapes.Ellipse
                {
                    Width = 6,
                    Height = 6,
                    Fill = ThemeHelper.Brush(isLive ? p.Ok : tc.Accent),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(dot, 2);
                row.Children.Add(dot);
            }

            var btn = new Button
            {
                Style = (Style)Application.Current.Resources["YYFluentButtonStyle"],
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Height = 38,
                MinHeight = 38,
                Padding = new Thickness(10, 0, 10, 0),
                Content = row,
            };
            if (isAdded)
            {
                btn.Background = ThemeHelper.Brush(tc.Soft);
                btn.BorderBrush = ThemeHelper.Brush(tc.Edge);
            }
            string tip = !string.IsNullOrWhiteSpace(prCap.Blurb)
                ? prCap.Blurb
                : $"{L10n.T("推荐模型：")}{prCap.DefaultModel}";
            if (isAdded) tip = $"{L10n.T("已添加")} · {tip}";
            ToolTipService.SetToolTip(btn, tip);
            btn.Click += (_, _) => ChoosePreset(prCap);
            return btn;
        }

        void RebuildCatalog()
        {
            if (!dialogOpen) return;
            catalogStack.Children.Clear();
            string[] terms = (searchBox.Text ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);

            var allPresets = CompanionClient.Presets.Where(x => !string.Equals(x.Id, "custom", StringComparison.OrdinalIgnoreCase)).ToList();
            var signIns = allPresets.Where(x => x.Group == "signIn" && Matches(x, terms)).ToList();
            var keys = allPresets.Where(x => x.Group == "key" && Matches(x, terms)).ToList();
            var locals = allPresets.Where(x => x.Group == "local" && Matches(x, terms)).ToList();

            if (signIns.Count > 0)
            {
                catalogStack.Children.Add(new TextBlock
                {
                    Text = L10n.T("一键登录"),
                    FontSize = 11.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = ThemeHelper.Brush(p.T3),
                    Margin = new Thickness(2, 0, 0, -2),
                });
                foreach (var pr in signIns)
                {
                    var prCap = pr;
                    bool isAdded = addedSet.Contains(prCap.Id);
                    var cardGrid = new Grid { ColumnSpacing = 12 };
                    cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
                    cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    FrameworkElement icon = (FrameworkElement?)ThemeHelper.CreateBrandLogoImage(prCap.Id, dark, 22)
                        ?? YYIcons.Make("globe", 18, ThemeHelper.Brush(p.T2));
                    Grid.SetColumn(icon, 0);
                    cardGrid.Children.Add(icon);

                    var meta = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
                    meta.Children.Add(L10n.Skip(new TextBlock
                    {
                        Text = prCap.Name,
                        FontSize = 13,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = ThemeHelper.Brush(p.T1),
                    }));
                    meta.Children.Add(new TextBlock
                    {
                        Text = L10n.T(prCap.Blurb ?? "登录就能用，几百个模型一个账号"),
                        FontSize = 11.5,
                        Foreground = ThemeHelper.Brush(p.T2),
                    });
                    Grid.SetColumn(meta, 1);
                    cardGrid.Children.Add(meta);

                    var badge = new TextBlock
                    {
                        Text = L10n.T(isAdded ? "已添加 ›" : "选用 ›"),
                        FontSize = 11.5,
                        FontWeight = FontWeights.Medium,
                        Foreground = ThemeHelper.Brush(tc.Accent),
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    Grid.SetColumn(badge, 2);
                    cardGrid.Children.Add(badge);

                    var heroBtn = new Button
                    {
                        Style = (Style)Application.Current.Resources["YYFluentButtonStyle"],
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        Height = 48,
                        MinHeight = 48,
                        Padding = new Thickness(12, 0, 12, 0),
                        Background = ThemeHelper.Brush(isAdded ? tc.Soft : p.Card),
                        BorderBrush = ThemeHelper.Brush(isAdded ? tc.Edge : p.Line),
                        Content = cardGrid,
                    };
                    heroBtn.Click += (_, _) => ChoosePreset(prCap);
                    catalogStack.Children.Add(heroBtn);
                }
            }

            void AddGridGroup(string title, List<AiProviderPreset> items)
            {
                if (items.Count == 0) return;
                catalogStack.Children.Add(new TextBlock
                {
                    Text = L10n.T(title),
                    FontSize = 11.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = ThemeHelper.Brush(p.T3),
                    Margin = new Thickness(2, 2, 0, -2),
                });
                var grid = new Grid { ColumnSpacing = 7, RowSpacing = 6 };
                for (int c = 0; c < 3; c++)
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                int rows = (items.Count + 2) / 3;
                for (int r = 0; r < rows; r++)
                    grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                for (int i = 0; i < items.Count; i++)
                {
                    var tile = MakeTile(items[i]);
                    Grid.SetRow(tile, i / 3);
                    Grid.SetColumn(tile, i % 3);
                    grid.Children.Add(tile);
                }
                catalogStack.Children.Add(grid);
            }

            AddGridGroup("模型服务", keys);
            AddGridGroup("本机与局域网", locals);

            if (signIns.Count == 0 && keys.Count == 0 && locals.Count == 0)
            {
                catalogStack.Children.Add(new TextBlock
                {
                    Text = L10n.T("没找到。点下方「自己添加」，填上接口地址就行"),
                    FontSize = 12,
                    Foreground = ThemeHelper.Brush(p.T3),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 28, 0, 28),
                });
            }
        }

        searchBox.TextChanged += (_, _) => RebuildCatalog();
        RebuildCatalog();

        var customFooterRow = new Grid { ColumnSpacing = 8 };
        customFooterRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        customFooterRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        customFooterRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var plusIcon = YYIcons.Make("plus", 14, ThemeHelper.Brush(tc.Accent));
        Grid.SetColumn(plusIcon, 0);
        customFooterRow.Children.Add(plusIcon);

        var customLbl = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        customLbl.Children.Add(new TextBlock
        {
            Text = L10n.T("自己添加"),
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeHelper.Brush(p.T1),
        });
        customLbl.Children.Add(new TextBlock
        {
            Text = L10n.T("任何 OpenAI 兼容接口（vLLM / OneAPI / 中转站）"),
            FontSize = 11.5,
            Foreground = ThemeHelper.Brush(p.T3),
            VerticalAlignment = VerticalAlignment.Center,
        });
        Grid.SetColumn(customLbl, 1);
        customFooterRow.Children.Add(customLbl);

        var chevron = YYIcons.Make("chevron-right", 12, ThemeHelper.Brush(p.T3));
        Grid.SetColumn(chevron, 2);
        customFooterRow.Children.Add(chevron);

        var customBtn = new Button
        {
            Style = (Style)Application.Current.Resources["YYFluentButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Height = 40,
            MinHeight = 40,
            Padding = new Thickness(12, 0, 12, 0),
            Content = customFooterRow,
        };
        customBtn.Click += (_, _) =>
        {
            openCustomAfter = true;
            dialogOpen = false;
            dlgRef?.Hide();
        };

        var root = new StackPanel { Spacing = 8, Padding = new Thickness(20, 14, 20, 14) };
        root.Children.Add(YYChrome.MakeDialogHeader(
            L10n.T("添加书伴服务商"),
            L10n.T("搜索或按分类挑选，支持保留多个随时切换"),
            () => { dialogOpen = false; dlgRef?.Hide(); },
            dark,
            titleSize: 16));
        root.Children.Add(searchBox);
        root.Children.Add(new ScrollViewer
        {
            MaxHeight = 420,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = catalogStack,
        });
        root.Children.Add(new Border { Height = 1, Background = ThemeHelper.Brush(p.Line) });
        root.Children.Add(customBtn);

        dlgRef = YYChrome.CreateFluentDialog(xr, dark, 560, root, tint: _library.Prefs.Tint);
        dlgRef.Closed += (_, _) => dialogOpen = false;
        dlgRef.Opened += (_, _) =>
        {
            searchBox.Focus(FocusState.Programmatic);
            _ = Task.Run(async () =>
            {
                try
                {
                    var found = await CompanionClient.ProbeLocalProvidersAsync();
                    if (found.Count > 0 && !_disposed)
                    {
                        _uiQueue.TryEnqueue(() =>
                        {
                            if (!dialogOpen || _disposed) return;
                            foreach (var k in found.Keys) liveLocals.Add(k);
                            RebuildCatalog();
                        });
                    }
                }
                catch { }
            });
        };
        await dlgRef.ShowSafeAsync(_library.Prefs.Tint);

        if (openCustomAfter && !_disposed)
            await AddCustomCompanionProviderAsync(dark);
    }

    private async Task ShowCompanionPromptEditorAsync(CompanionPromptItem? existing, bool dark)
    {
        if (RootGrid.XamlRoot is not { } xr) return;
        var p = ThemeHelper.Palette(dark);
        var currentItems = CompanionPromptsHelper.GetItems(_library.Prefs);
        string fallbackCmd = existing?.Command ?? CompanionPromptsHelper.NextDefaultCommand(currentItems);

        ContentDialog? dlgRef = null;
        var cmdBox = new TextBox
        {
            Style = (Style)Application.Current.Resources["YYSingleLineTextBoxStyle"],
            Text = existing?.Command ?? fallbackCmd,
            PlaceholderText = fallbackCmd,
            MaxLength = CompanionPromptsHelper.MaxCommandLength,
        };
        var promptBox = new TextBox
        {
            Text = existing?.Prompt ?? "",
            PlaceholderText = L10n.T("如：用通俗的话总结这段的核心观点"),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 84,
            MaxLength = CompanionPromptsHelper.MaxLength,
        };
        var previewTb = new TextBlock
        {
            FontSize = 11,
            Foreground = ThemeHelper.Brush(p.T3),
        };
        void RefreshPreview()
        {
            string resolved = CompanionPromptsHelper.SanitizeCommand(cmdBox.Text, fallbackCmd, currentItems, existing?.Id);
            previewTb.Text = L10n.T($"在书伴输入框里键入 /{resolved} 即可触发");
        }
        cmdBox.TextChanged += (_, _) => RefreshPreview();
        RefreshPreview();

        var body = new StackPanel { Spacing = 12, Padding = new Thickness(20, 16, 20, 18) };
        body.Children.Add(YYChrome.MakeDialogHeader(
            existing is null ? "添加常用提问" : "编辑常用提问",
            "同时出现在书伴快捷建议与 / 快捷指令中",
            () => dlgRef?.Hide(),
            dark));

        body.Children.Add(new TextBlock { Text = L10n.T("快捷指令名（不含 /）"), FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T2) });
        body.Children.Add(cmdBox);
        body.Children.Add(previewTb);
        body.Children.Add(new TextBlock { Text = L10n.T("提问内容"), FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T2), Margin = new Thickness(0, 4, 0, 0) });
        body.Children.Add(promptBox);

        var foot = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 0, 0) };
        foot.Children.Add(YYChrome.MakeDialogButton("取消", () => dlgRef?.Hide(), dark, _library.Prefs.Tint, "secondary"));
        var saveBtn = YYChrome.MakeDialogButton("保存", () =>
        {
            string text = (promptBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(text)) return;
            var item = new CompanionPromptItem
            {
                Id = existing?.Id ?? Guid.NewGuid().ToString("N"),
                Command = cmdBox.Text ?? fallbackCmd,
                Prompt = text,
            };
            if (CompanionPromptsHelper.Upsert(_library.Prefs, item))
            {
                _library.SavePrefs();
                dlgRef?.Hide();
                ShowSettingsSavedToast(existing is null ? "已添加常用提问" : "已保存常用提问");
                RenderActivePage();
            }
            else
            {
                _ownerWindow.ShowToast("已有相同的提问内容或已达上限");
            }
        }, dark, _library.Prefs.Tint, "primary");
        foot.Children.Add(saveBtn);
        body.Children.Add(foot);

        dlgRef = YYChrome.CreateFluentDialog(xr, dark, 440, body, tint: _library.Prefs.Tint);
        await dlgRef.ShowSafeAsync(_library.Prefs.Tint);
    }

    private async Task ShowCompanionModelPickerDialogAsync(
        string providerId,
        string? overrideUrl,
        string? overrideKey,
        TextBox targetModelBox,
        bool dark)
    {
        if (RootGrid.XamlRoot is not { } xr) return;
        var p = ThemeHelper.Palette(dark);
        var tc = ThemeHelper.TintColors(_library.Prefs.Tint, dark);
        var preset = CompanionClient.FindPreset(providerId);
        string provTitle = preset?.Name
            ?? (_library.Prefs.AiCustomProviderNames.TryGetValue(providerId, out var cn) && !string.IsNullOrWhiteSpace(cn) ? cn : providerId);

        ContentDialog? dlgRef = null;
        bool dialogOpen = true;
        var liveModels = new List<string>(_library.Companion.GetCachedModels(providerId));

        var statusTb = new TextBlock
        {
            FontSize = 11.5,
            Foreground = ThemeHelper.Brush(p.T3),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var refreshBtn = new Button
        {
            Style = (Style)Application.Current.Resources["YYFluentButtonStyle"],
            Height = 26,
            MinHeight = 26,
            FontSize = 11.5,
            Padding = new Thickness(10, 0, 10, 0),
            Content = L10n.T("刷新列表"),
        };

        var searchBox = YYChrome.MakeSearchBox("搜索模型 ID、厂商，或输入自定义名按 Enter…");
        var listStack = new StackPanel { Spacing = 5, Padding = new Thickness(0, 2, 4, 8) };

        void PickModel(string modelId)
        {
            string clean = modelId.Trim();
            if (string.IsNullOrEmpty(clean)) return;
            targetModelBox.Text = clean;
            _library.Companion.RecordModelUsed(providerId, clean);
            dialogOpen = false;
            dlgRef?.Hide();
        }

        void RebuildList()
        {
            if (!dialogOpen) return;
            listStack.Children.Clear();

            string query = (searchBox.Text ?? "").Trim();
            string activeModel = (targetModelBox.Text ?? "").Trim();

            // Gather deduplicated candidate pool
            var allPool = new List<string>();
            var poolSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void AddToPool(string? m)
            {
                if (!string.IsNullOrWhiteSpace(m) && poolSet.Add(m.Trim()))
                    allPool.Add(m.Trim());
            }

            if (!string.IsNullOrEmpty(activeModel)) AddToPool(activeModel);
            foreach (var st in _library.Prefs.StarredAiModels)
            {
                string pref = providerId + ":";
                if (st.StartsWith(pref, StringComparison.OrdinalIgnoreCase)) AddToPool(st[pref.Length..]);
            }
            if (_library.Prefs.AiRecentModels.TryGetValue(providerId, out var recents))
                foreach (var r in recents) AddToPool(r);
            AddToPool(preset?.DefaultModel);
            AddToPool(preset?.DefaultFastModel);
            foreach (var m in liveModels) AddToPool(m);

            bool Matches(string m) =>
                string.IsNullOrEmpty(query)
                || m.Contains(query, StringComparison.OrdinalIgnoreCase)
                || CompanionClient.InferModelMaker(m).Contains(query, StringComparison.OrdinalIgnoreCase);

            var filtered = allPool.Where(Matches).ToList();

            // Custom direct-entry row when user typed a model ID not listed exactly
            if (!string.IsNullOrEmpty(query) && !allPool.Contains(query, StringComparer.OrdinalIgnoreCase))
            {
                string customCap = query;
                var customBtn = new Button
                {
                    Style = (Style)Application.Current.Resources["YYFluentSoftAccentButtonStyle"],
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Height = 34,
                    MinHeight = 34,
                    Content = L10n.Skip(new TextBlock
                    {
                        Text = $"{L10n.T("使用自定义模型：")}{customCap}",
                        FontSize = 12,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    }),
                };
                customBtn.Click += (_, _) => PickModel(customCap);
                listStack.Children.Add(customBtn);
            }

            var rendered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddGroup(string groupTitle, IEnumerable<string> items, int cap = 120)
            {
                var groupItems = items.Where(m => !rendered.Contains(m)).ToList();
                if (groupItems.Count == 0) return;

                listStack.Children.Add(new TextBlock
                {
                    Text = groupTitle,
                    FontSize = 11,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = ThemeHelper.Brush(p.T3),
                    Margin = new Thickness(2, listStack.Children.Count == 0 ? 0 : 6, 0, 2),
                });

                int shown = 0;
                foreach (var mid in groupItems)
                {
                    if (shown >= cap) break;
                    rendered.Add(mid);
                    shown++;
                    string mCap = mid;
                    bool isCur = string.Equals(activeModel, mCap, StringComparison.OrdinalIgnoreCase);
                    bool starred = _library.Companion.IsModelStarred(providerId, mCap);
                    string maker = CompanionClient.InferModelMaker(mCap);
                    string? badge = CompanionClient.InferRouteBadge(providerId, mCap);

                    var rowGrid = new Grid { ColumnSpacing = 8 };
                    rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var starBtn = new Button
                    {
                        Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
                        Width = 24,
                        Height = 24,
                        MinWidth = 24,
                        MinHeight = 24,
                        Padding = new Thickness(0),
                        Content = L10n.Skip(new TextBlock
                        {
                            Text = starred ? "★" : "☆",
                            FontSize = 12,
                            Foreground = ThemeHelper.Brush(starred ? tc.Accent : p.T3),
                            VerticalAlignment = VerticalAlignment.Center,
                        }),
                    };
                    ToolTipService.SetToolTip(starBtn, L10n.T(starred ? "取消星标" : "星标置顶"));
                    starBtn.Click += (_, _) =>
                    {
                        _library.Companion.ToggleStarModel(providerId, mCap);
                        RebuildList();
                    };

                    var titleBtn = new Button
                    {
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Left,
                        Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
                        BorderThickness = new Thickness(0),
                        Padding = new Thickness(2, 4, 2, 4),
                        UseSystemFocusVisuals = false,
                        Content = L10n.Skip(new TextBlock
                        {
                            Text = mCap,
                            FontSize = 12.5,
                            FontWeight = isCur ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                            Foreground = ThemeHelper.Brush(isCur ? tc.Accent : p.T1),
                            MaxLines = 1,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                            VerticalAlignment = VerticalAlignment.Center,
                        }),
                    };
                    titleBtn.Click += (_, _) => PickModel(mCap);

                    var rightMeta = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
                    if (maker != "其它")
                    {
                        rightMeta.Children.Add(L10n.Skip(new TextBlock
                        {
                            Text = maker,
                            FontSize = 10.5,
                            Foreground = ThemeHelper.Brush(p.T3),
                            VerticalAlignment = VerticalAlignment.Center,
                        }));
                    }
                    if (!string.IsNullOrEmpty(badge))
                    {
                        rightMeta.Children.Add(new Border
                        {
                            CornerRadius = new CornerRadius(4),
                            Background = ThemeHelper.Brush(p.Inset),
                            Padding = new Thickness(5, 1, 5, 1),
                            VerticalAlignment = VerticalAlignment.Center,
                            Child = new TextBlock
                            {
                                Text = L10n.T(badge),
                                FontSize = 10,
                                Foreground = ThemeHelper.Brush(p.T2),
                            },
                        });
                    }
                    if (isCur)
                    {
                        rightMeta.Children.Add(L10n.Skip(new TextBlock
                        {
                            Text = "✓",
                            FontSize = 12,
                            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                            Foreground = ThemeHelper.Brush(tc.Accent),
                            VerticalAlignment = VerticalAlignment.Center,
                        }));
                    }

                    Grid.SetColumn(starBtn, 0);
                    Grid.SetColumn(titleBtn, 1);
                    Grid.SetColumn(rightMeta, 2);
                    rowGrid.Children.Add(starBtn);
                    rowGrid.Children.Add(titleBtn);
                    rowGrid.Children.Add(rightMeta);

                    listStack.Children.Add(new Border
                    {
                        CornerRadius = new CornerRadius(6),
                        Background = ThemeHelper.Brush(isCur ? tc.Soft : p.Card),
                        BorderBrush = ThemeHelper.Brush(isCur ? tc.Edge : p.Line),
                        BorderThickness = new Thickness(1),
                        Padding = new Thickness(6, 3, 10, 3),
                        Child = rowGrid,
                    });
                }

                if (groupItems.Count > cap)
                {
                    listStack.Children.Add(new TextBlock
                    {
                        Text = L10n.T($"还有 {groupItems.Count - cap} 个模型，输入关键词可快速筛选"),
                        FontSize = 11,
                        Foreground = ThemeHelper.Brush(p.T3),
                        Margin = new Thickness(4, 4, 0, 2),
                    });
                }
            }

            var starredItems = filtered.Where(m => _library.Companion.IsModelStarred(providerId, m)).ToList();
            var recentSet = _library.Prefs.AiRecentModels.TryGetValue(providerId, out var rList)
                ? new HashSet<string>(rList, StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var recentItems = filtered.Where(m => recentSet.Contains(m)).ToList();
            var recommendedItems = new List<string>();
            if (!string.IsNullOrWhiteSpace(preset?.DefaultModel) && filtered.Contains(preset.DefaultModel, StringComparer.OrdinalIgnoreCase))
                recommendedItems.Add(preset.DefaultModel);
            if (!string.IsNullOrWhiteSpace(preset?.DefaultFastModel) && filtered.Contains(preset.DefaultFastModel, StringComparer.OrdinalIgnoreCase))
                recommendedItems.Add(preset.DefaultFastModel);

            AddGroup(L10n.T("已星标"), starredItems);
            AddGroup(L10n.T("最近用过"), recentItems);
            AddGroup(L10n.T("推荐模型"), recommendedItems);
            AddGroup(L10n.T($"全部模型 ({filtered.Count})"), filtered, cap: 120);

            if (listStack.Children.Count == 0)
            {
                listStack.Children.Add(new TextBlock
                {
                    Text = L10n.T("没有匹配的模型，直接输入模型 ID 并按 Enter 即可应用"),
                    FontSize = 12,
                    Foreground = ThemeHelper.Brush(p.T3),
                    Margin = new Thickness(4, 12, 4, 12),
                });
            }
        }

        async Task FetchLatestAsync()
        {
            refreshBtn.IsEnabled = false;
            statusTb.Text = L10n.T("正在从服务商拉取最新模型列表…");
            try
            {
                var fetched = await _library.Companion.ListModelsAsync(providerId, overrideUrl, overrideKey);
                if (_disposed || !dialogOpen) return;
                if (fetched.Count > 0)
                {
                    _library.Companion.SaveCachedModels(providerId, fetched);
                    liveModels.Clear();
                    liveModels.AddRange(fetched);
                    statusTb.Text = L10n.T($"已同步 {fetched.Count} 个可用模型");
                    RebuildList();
                }
                else
                {
                    statusTb.Text = liveModels.Count > 0
                        ? L10n.T($"未获取到新列表，当前展示 {liveModels.Count} 个缓存模型")
                        : L10n.T("接口未返回模型列表，可选择推荐项或输入自定义模型名");
                }
            }
            catch (Exception ex)
            {
                if (!_disposed && dialogOpen)
                    statusTb.Text = L10n.T($"拉取失败：{ex.Message}");
            }
            finally
            {
                if (!_disposed && dialogOpen)
                    refreshBtn.IsEnabled = true;
            }
        }

        refreshBtn.Click += (_, _) => _ = FetchLatestAsync();
        searchBox.TextChanged += (_, _) => RebuildList();
        searchBox.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                string q = (searchBox.Text ?? "").Trim();
                if (!string.IsNullOrEmpty(q))
                {
                    PickModel(q);
                    e.Handled = true;
                }
            }
        };

        var statusRow = new Grid { ColumnSpacing = 8 };
        statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(statusTb, 0);
        Grid.SetColumn(refreshBtn, 1);
        statusRow.Children.Add(statusTb);
        statusRow.Children.Add(refreshBtn);

        var root = new StackPanel { Spacing = 10, Padding = new Thickness(20, 16, 20, 16) };
        root.Children.Add(YYChrome.MakeDialogHeader(
            $"{provTitle} · {L10n.T("选择模型")}",
            null,
            () => { dialogOpen = false; dlgRef?.Hide(); },
            dark,
            leading: ThemeHelper.CreateBrandLogoImage(providerId, dark, 20),
            titleSize: 16));
        root.Children.Add(searchBox);
        root.Children.Add(statusRow);
        root.Children.Add(new ScrollViewer { MaxHeight = 400, Content = listStack });

        statusTb.Text = liveModels.Count > 0
            ? L10n.T($"已缓存 {liveModels.Count} 个模型 · 支持按名称或厂商筛选")
            : L10n.T("正在拉取可用模型…");
        RebuildList();

        dlgRef = YYChrome.CreateFluentDialog(xr, dark, 500, root, tint: _library.Prefs.Tint);
        dlgRef.Closed += (_, _) => dialogOpen = false;
        dlgRef.Opened += (_, _) =>
        {
            searchBox.Focus(FocusState.Programmatic);
            if (liveModels.Count == 0) _ = FetchLatestAsync();
        };
        await dlgRef.ShowSafeAsync(_library.Prefs.Tint);
    }

    private void BuildSyncPage(bool dark)
    {
        PageBodyStack.Children.Add(MakeSectionHeader("同步方式", dark, first: true));

        var methodCombo = new ComboBox { Width = 260 };
        (string Key, string Lbl)[] methods = [("none", "停止同步"), ("webdav", "WebDAV（坚果云、群晖、Nextcloud…）"), ("folder", "同步文件夹（OneDrive / iCloud）"), ("gdrive", "Google Drive")];
        foreach (var (_, l) in methods) methodCombo.Items.Add(L10n.T(l));
        methodCombo.SelectedIndex = Math.Max(0, Array.FindIndex(methods, x => x.Key == _library.Prefs.SyncMethod));
        methodCombo.SelectionChanged += (_, _) =>
        {
            if (methodCombo.SelectedIndex >= 0)
            {
                string chosen = methods[methodCombo.SelectedIndex].Key;
                if (chosen == "none")
                {
                    _library.Sync.StopSync();
                }
                else
                {
                    _library.Prefs.SyncMethod = chosen;
                    _library.SavePrefs();
                }
                RenderActivePage();
            }
        };
        PageBodyStack.Children.Add(new FluentSettingsCard("同步方式", _library.Sync.StatusSummary(), "sync-arrows", methodCombo, dark: dark));

        if (_library.Prefs.SyncMethod == "webdav")
        {
            var davUrl = new TextBox { Style = (Style)Application.Current.Resources["YYSingleLineTextBoxStyle"], Width = 260, Text = _library.Prefs.WebDavUrl };
            davUrl.TextChanged += (_, _) => { _library.Prefs.WebDavUrl = davUrl.Text ?? ""; _library.SavePrefs(); };
            string davBrand = _library.Prefs.WebDavUrl.Contains("jianguoyun", StringComparison.OrdinalIgnoreCase) ? "jianguoyun" : "webdav";
            PageBodyStack.Children.Add(new FluentSettingsCard(
                "地址",
                "坚果云默认：https://dav.jianguoyun.com/dav/",
                "server",
                davUrl,
                dark: dark,
                customIcon: ThemeHelper.CreateBrandLogoImage(davBrand, dark, 20)));

            var davUser = new TextBox { Style = (Style)Application.Current.Resources["YYSingleLineTextBoxStyle"], Width = 260, Text = _library.Prefs.WebDavUser };
            davUser.TextChanged += (_, _) => { _library.Prefs.WebDavUser = davUser.Text ?? ""; _library.SavePrefs(); };
            PageBodyStack.Children.Add(new FluentSettingsCard("账号", null, "pencil", davUser, dark: dark));

            var davPwd = new PasswordBox { Style = (Style)Application.Current.Resources["YYCompactPasswordBoxStyle"], Width = 260, Password = _library.Secrets.Get("sync.webdav.password") ?? "" };
            davPwd.PasswordChanged += (_, _) => _library.Secrets.Set("sync.webdav.password", davPwd.Password);
            PageBodyStack.Children.Add(new FluentSettingsCard("密码", "应用专用密码，DPAPI 加密存储在本地", "lock", davPwd, dark: dark));

            var checkDavBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentButtonStyle"], Content = L10n.T("检查连接") };
            checkDavBtn.Click += async (_, _) =>
            {
                if (RootGrid.XamlRoot is null) return;
                checkDavBtn.IsEnabled = false;
                WebDavCheckReport report;
                try
                {
                    string folder = string.IsNullOrWhiteSpace(_library.Prefs.WebDavFolder) ? "语阅" : _library.Prefs.WebDavFolder;
                    report = await WebDavCheckService.RunAsync(
                        _library.Prefs.WebDavUrl,
                        _library.Prefs.WebDavUser,
                        _library.Secrets.Get("sync.webdav.password") ?? "",
                        folder);
                }
                catch (Exception ex)
                {
                    if (!_disposed) _ownerWindow.ShowToast($"WebDAV 检查失败：{ex.Message}");
                    return;
                }
                finally
                {
                    checkDavBtn.IsEnabled = true;
                }
                if (_disposed || RootGrid.XamlRoot is not { } xr) return;
                var p = Design.ThemeHelper.Palette(dark);
                var tc = Design.ThemeHelper.TintColors(_library.Prefs.Tint, dark);
                ContentDialog? dlgRef = null;
                var box = new StackPanel { Spacing = 14, Padding = new Thickness(20, 16, 20, 18) };
                box.Children.Add(Design.YYChrome.MakeDialogHeader("检查连接", report.AllPassed ? "全部检查通过" : "发现连接问题", () => dlgRef?.Hide(), dark));
                box.Children.Add(WebDavCheckService.BuildCheckCard(report, p, tc));
                dlgRef = Design.YYChrome.CreateFluentDialog(xr, dark, 480, box, tint: _library.Prefs.Tint);
                await dlgRef.ShowSafeAsync(_library.Prefs.Tint);
            };
            PageBodyStack.Children.Add(new FluentSettingsCard("检查连接", "逐项检测地址、账号、目录权限与读写速度", "check-circle", checkDavBtn, dark: dark));
        }
        else if (_library.Prefs.SyncMethod == "folder")
        {
            var pickBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentButtonStyle"], Content = L10n.T("选一个文件夹…") };
            pickBtn.Click += async (_, _) =>
            {
                string? dir = await NativePickerHelper.PickSingleFolderAsync(_ownerWindow);
                if (!string.IsNullOrEmpty(dir))
                {
                    _library.Prefs.SyncLocalFolder = dir;
                    _library.SavePrefs();
                    RenderActivePage();
                }
            };
            PageBodyStack.Children.Add(new FluentSettingsCard(
                "同步文件夹",
                string.IsNullOrWhiteSpace(_library.Prefs.SyncLocalFolder) ? "选择 OneDrive / Dropbox / 坚果云本地目录" : _library.Prefs.SyncLocalFolder,
                "folder",
                pickBtn,
                dark: dark));
        }
        else if (_library.Prefs.SyncMethod == "gdrive")
        {
            var gClientIdBox = new TextBox
            {
                Style = (Style)Application.Current.Resources["YYSingleLineTextBoxStyle"],
                Width = 260,
                Text = _library.Prefs.GoogleClientId,
                PlaceholderText = "….apps.googleusercontent.com",
            };
            gClientIdBox.TextChanged += (_, _) =>
            {
                _library.Prefs.GoogleClientId = (gClientIdBox.Text ?? "").Trim();
                _library.SavePrefs();
            };
            PageBodyStack.Children.Add(new FluentSettingsCard(
                "Google OAuth Client ID",
                "填写 Google Cloud 桌面端 OAuth Client ID（以 .apps.googleusercontent.com 结尾）",
                "lock",
                gClientIdBox,
                dark: dark));

            var gSecretBox = new PasswordBox
            {
                Style = (Style)Application.Current.Resources["YYCompactPasswordBoxStyle"],
                Width = 260,
                Password = _library.Secrets.Get("sync.google.secret") ?? "",
                PlaceholderText = "GOCSPX-…（选填）",
            };
            gSecretBox.PasswordChanged += (_, _) => _library.Secrets.Set("sync.google.secret", (gSecretBox.Password ?? "").Trim());
            PageBodyStack.Children.Add(new FluentSettingsCard(
                "Google OAuth Client Secret（选填）",
                "部分 Google Cloud 桌面端凭据换取令牌时需附带 Client Secret，DPAPI 加密保存于本机",
                "lock",
                gSecretBox,
                dark: dark));

            bool gConnected = !string.IsNullOrWhiteSpace(_library.Secrets.Get(SyncService.GoogleRefreshSecretKey));
            string gDesc = gConnected
                ? (!string.IsNullOrWhiteSpace(_library.Prefs.GoogleEmail)
                    ? $"已连接账号：{_library.Prefs.GoogleEmail}"
                    : "已授权 Google Drive 应用隐藏空间（appDataFolder）")
                : "通过系统浏览器完成 OAuth 授权，仅访问语阅专属隐藏同步目录";

            var gActs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var gCancelBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
                Content = L10n.T("取消"),
                Visibility = Visibility.Collapsed,
            };
            gCancelBtn.Click += (_, _) =>
            {
                try { _gdriveAuthCts?.Cancel(); } catch { }
            };
            var gSignInBtn = new Button
            {
                Style = (Style)Application.Current.Resources[gConnected ? "YYFluentButtonStyle" : "YYFluentAccentButtonStyle"],
                Content = L10n.T(gConnected ? "重新授权" : "登录 Google Drive"),
            };
            gSignInBtn.Click += async (_, _) =>
            {
                try { _gdriveAuthCts?.Cancel(); _gdriveAuthCts?.Dispose(); } catch { }
                var cts = new CancellationTokenSource();
                _gdriveAuthCts = cts;

                gSignInBtn.IsEnabled = false;
                gSignInBtn.Content = L10n.T("等待授权…");
                gCancelBtn.Visibility = Visibility.Visible;
                try
                {
                    await _library.Sync.ConnectGoogleDriveAsync(cts.Token);
                    if (_disposed) return;
                    App.CurrentApp.NotifyShelfRefresh();
                    _ownerWindow.ShowToast(string.IsNullOrWhiteSpace(_library.Prefs.GoogleEmail)
                        ? "已连接 Google Drive"
                        : $"已连接 Google Drive（{_library.Prefs.GoogleEmail}）");
                }
                catch (OperationCanceledException)
                {
                    if (!_disposed)
                        _ownerWindow.ShowToast("已取消 Google 授权");
                }
                catch (Exception ex)
                {
                    if (!_disposed)
                        _ownerWindow.ShowToast($"Google Drive 连接失败：{ex.Message}");
                }
                finally
                {
                    if (ReferenceEquals(_gdriveAuthCts, cts))
                    {
                        try { cts.Dispose(); } catch { }
                        _gdriveAuthCts = null;
                    }
                    gSignInBtn.IsEnabled = true;
                    gSignInBtn.Content = L10n.T(gConnected ? "重新授权" : "登录 Google Drive");
                    gCancelBtn.Visibility = Visibility.Collapsed;
                    if (!_disposed && _activePage == "sync") RenderActivePage();
                }
            };
            gActs.Children.Add(gCancelBtn);
            gActs.Children.Add(gSignInBtn);

            if (gConnected)
            {
                var gDisconnectBtn = new Button
                {
                    Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
                    Content = L10n.T("断开连接"),
                    Foreground = ThemeHelper.Brush(ThemeHelper.Palette(dark).Danger),
                };
                gDisconnectBtn.Click += (_, _) =>
                {
                    _library.Sync.StopSync();
                    _ownerWindow.ShowToast("已断开 Google Drive 账号连接");
                    RenderActivePage();
                };
                gActs.Children.Add(gDisconnectBtn);
            }

            PageBodyStack.Children.Add(new FluentSettingsCard(
                "Google Drive 账号",
                gDesc,
                "cloud",
                gActs,
                dark: dark));
        }

        if (_library.Sync.IsEnabled)
        {
            var syncNowBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentAccentButtonStyle"], Content = L10n.T("立即同步") };
            syncNowBtn.Click += async (_, _) =>
            {
                syncNowBtn.IsEnabled = false;
                try
                {
                    await _library.Sync.SyncNowAsync();
                    if (!_disposed)
                    {
                        _ownerWindow.ShowToast(_library.Sync.StatusSummary());
                        if (_activePage == "sync") RenderActivePage();
                    }
                }
                finally
                {
                    syncNowBtn.IsEnabled = true;
                }
            };
            PageBodyStack.Children.Add(new FluentSettingsCard("立即同步", "同步进度、笔记、生词与书架", "cloud", syncNowBtn, dark: dark));

            var conflictBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentButtonStyle"], Content = L10n.T("检查冲突…") };
            conflictBtn.Click += async (_, _) =>
            {
                NoteConflict[] conflicts;
                try { conflicts = _library.Core.NoteConflicts(); } catch { conflicts = []; }
                if (conflicts.Length == 0)
                {
                    _ownerWindow.ShowToast("当前没有待处理的笔记冲突");
                    return;
                }
                if (RootGrid.XamlRoot is { } xr)
                    await SnapshotAndConflictDialogs.ShowNoteConflictsSheetAsync(xr, _library, () => App.CurrentApp.NotifyShelfRefresh());
            };
            PageBodyStack.Children.Add(new FluentSettingsCard("笔记冲突", "若两台设备同时改了同一条笔记，可在这里对比保留", "checklist-lines", conflictBtn, dark: dark));

            // Granular Sync Parts Expander (1:1 macOS)
            var partsExp = new FluentSettingsExpander("同步内容", "选择要同步到其他设备的内容", "sliders", dark: dark);
            ToggleSwitch MakePartSw(bool init, Action<bool> set)
            {
                var sw = new ToggleSwitch { IsOn = init };
                sw.Toggled += (_, _) => { set(sw.IsOn); _library.SavePrefs(); };
                return sw;
            }
            partsExp.AddKeyValueRow("进度和书签", "每本书读到的位置、读过状态与阅读时长", MakePartSw(_library.Prefs.SyncReading, v => _library.Prefs.SyncReading = v));
            partsExp.AddKeyValueRow("笔记和生词", "划线、想法、合集与生词本", MakePartSw(_library.Prefs.SyncNotes, v => _library.Prefs.SyncNotes = v));
            partsExp.AddKeyValueRow("设置和替换规则", "自定义纸张、排版偏好与替换规则", MakePartSw(_library.Prefs.SyncSettings, v => _library.Prefs.SyncSettings = v));
            partsExp.AddKeyValueRow("书文件", "EPUB / PDF / TXT 原文件", MakePartSw(_library.Prefs.SyncFiles, v => _library.Prefs.SyncFiles = v));
            partsExp.AddKeyValueRow("有声书录音", "音频体积较大，网盘空间充足时再开", MakePartSw(_library.Prefs.SyncRecordings, v => _library.Prefs.SyncRecordings = v));
            PageBodyStack.Children.Add(partsExp);
        }

        // Connected devices list
        var devices = _library.GetDevices();
        if (devices.Count > 0)
        {
            PageBodyStack.Children.Add(MakeSectionHeader($"已同步设备 ({devices.Count})", dark));
            foreach (var dev in devices)
            {
                string plat = dev.Platform.ToLowerInvariant() switch
                {
                    "macos" => "Mac",
                    "ios" => "iPhone",
                    "ipados" => "iPad",
                    "windows" => "Windows",
                    "android" => "Android",
                    _ => dev.Platform,
                };
                string subtitle = dev.ThisDevice ? "这台" : plat;
                if (!string.IsNullOrWhiteSpace(dev.LastBook))
                    subtitle += $" · 最近在读《{dev.LastBook}》";
                PageBodyStack.Children.Add(new FluentSettingsCard(dev.Name, subtitle, "laptop", dark: dark));
            }
        }
    }

    private void BuildExportPage(bool dark)
    {
        PageBodyStack.Children.Add(MakeSectionHeader("导出目标", dark, first: true));

        var obsPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var obsPickBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentButtonStyle"], Content = L10n.T("选一个文件夹…") };
        obsPickBtn.Click += async (_, _) =>
        {
            string? dir = await NativePickerHelper.PickSingleFolderAsync(_ownerWindow);
            if (!string.IsNullOrEmpty(dir))
            {
                _library.Prefs.ObsidianFolder = dir;
                _library.SavePrefs();
                RenderActivePage();
            }
        };
        var obsExportBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentAccentButtonStyle"], Content = L10n.T("导出") };
        obsExportBtn.Click += async (_, _) =>
        {
            try
            {
                var (b, m) = await _library.NoteExports.ExportToObsidianAsync();
                _ownerWindow.ShowToast($"已导出 {b} 本书、{m} 条笔记到 Obsidian");
            }
            catch (Exception ex) { _ownerWindow.ShowToast($"Obsidian 导出失败：{ex.Message}"); }
        };
        obsPanel.Children.Add(obsPickBtn);
        obsPanel.Children.Add(obsExportBtn);
        PageBodyStack.Children.Add(new FluentSettingsCard(
            "Obsidian",
            string.IsNullOrWhiteSpace(_library.Prefs.ObsidianFolder) ? "未连接" : _library.Prefs.ObsidianFolder,
            "obsidian",
            obsPanel,
            dark: dark,
            customIcon: ThemeHelper.CreateExportBrandTile("obsidian", dark, 28)));

        // flomo webhook + test export
        string curFlomo = _library.Secrets.Get("export.flomo.url");
        if (string.IsNullOrWhiteSpace(curFlomo)) curFlomo = _library.Prefs.FlomoWebhookUrl ?? "";
        var flomoPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var flomoBox = new TextBox
        {
            Style = (Style)Application.Current.Resources["YYSingleLineTextBoxStyle"],
            Width = 220,
            Text = curFlomo,
            PlaceholderText = "API 链接",
        };
        flomoBox.TextChanged += (_, _) =>
        {
            string v = (flomoBox.Text ?? "").Trim();
            _library.Prefs.FlomoWebhookUrl = v;
            _library.Secrets.Set("export.flomo.url", v);
            _library.SavePrefs();
        };
        var flomoExportBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentButtonStyle"], Content = L10n.T("发送") };
        flomoExportBtn.Click += async (_, _) =>
        {
            if (!NoteExportService.ValidateFlomoWebhook((flomoBox.Text ?? "").Trim()))
            {
                _ownerWindow.ShowToast("请先输入有效的 flomo API 链接（以 https://flomoapp.com/iwh/ 开头）");
                return;
            }
            try
            {
                var (b, m) = await _library.NoteExports.ExportToFlomoAsync();
                _ownerWindow.ShowToast($"已发送 {b} 本书、{m} 条笔记到 flomo");
            }
            catch (Exception ex) { _ownerWindow.ShowToast($"flomo 发送失败：{ex.Message}"); }
        };
        flomoPanel.Children.Add(flomoBox);
        flomoPanel.Children.Add(flomoExportBtn);
        PageBodyStack.Children.Add(new FluentSettingsCard(
            "flomo",
            "每条划线一条 memo，带 #语阅 和书名；再发一次只发新的。",
            "flomo",
            flomoPanel,
            dark: dark,
            customIcon: ThemeHelper.CreateExportBrandTile("flomo", dark, 28)));

        // Notion integration
        var notionExp = new FluentSettingsExpander(
            "Notion",
            string.IsNullOrWhiteSpace(_library.Prefs.NotionPageTitle) ? "填写令牌与目标页面后即可导出" : $"目标页面：{_library.Prefs.NotionPageTitle}",
            "notion",
            dark: dark,
            customIcon: ThemeHelper.CreateExportBrandTile("notion", dark, 28));
        var notionTokenBox = new PasswordBox
        {
            Style = (Style)Application.Current.Resources["YYCompactPasswordBoxStyle"],
            Width = 240,
            Password = _library.Secrets.Get("export.notion.token") ?? "",
            PlaceholderText = "ntn_… / secret_…",
        };
        notionTokenBox.PasswordChanged += (_, _) => _library.Secrets.Set("export.notion.token", notionTokenBox.Password);
        notionExp.AddKeyValueRow("令牌", "在 Notion Integrations 中创建并授权给目标页面", notionTokenBox);

        var notionPageBox = new TextBox
        {
            Style = (Style)Application.Current.Resources["YYSingleLineTextBoxStyle"],
            Width = 240,
            Text = _library.Prefs.NotionPageId,
            PlaceholderText = "粘贴 Notion 页面链接或 32 位 ID",
        };
        notionPageBox.TextChanged += (_, _) =>
        {
            string raw = (notionPageBox.Text ?? "").Trim();
            _library.Prefs.NotionPageId = NoteExportService.ExtractNotionPageId(raw) ?? raw;
            _library.SavePrefs();
        };
        notionExp.AddKeyValueRow("目标页面", "每本书会自动在该页面下生成独立子页", notionPageBox);

        var notionBtns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var notionVerifyBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentButtonStyle"], Content = L10n.T("检查连接") };
        notionVerifyBtn.Click += async (_, _) =>
        {
            var (ok, title, msg) = await _library.NoteExports.VerifyNotionAsync(notionTokenBox.Password, notionPageBox.Text ?? "");
            if (ok) { _library.Prefs.NotionPageTitle = title; _library.SavePrefs(); }
            _ownerWindow.ShowToast(ok ? $"Notion 连接成功：{title}" : $"Notion 连接失败：{msg}");
        };
        var notionSyncBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentAccentButtonStyle"], Content = L10n.T("导出") };
        notionSyncBtn.Click += async (_, _) =>
        {
            try
            {
                var (b, m) = await _library.NoteExports.ExportToNotionAsync();
                _ownerWindow.ShowToast($"已导出 {b} 本书、{m} 条笔记到 Notion");
            }
            catch (Exception ex) { _ownerWindow.ShowToast($"Notion 导出失败：{ex.Message}"); }
        };
        notionBtns.Children.Add(notionVerifyBtn);
        notionBtns.Children.Add(notionSyncBtn);
        notionExp.AddKeyValueRow("导出", "增量同步，再导一次只写新增的笔记", notionBtns);
        PageBodyStack.Children.Add(notionExp);

        // Xinzhi integration
        var xzPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var xzTokenBox = new PasswordBox
        {
            Style = (Style)Application.Current.Resources["YYCompactPasswordBoxStyle"],
            Width = 210,
            Password = _library.Secrets.Get("export.xinzhi.token") ?? "",
            PlaceholderText = "令牌",
        };
        xzTokenBox.PasswordChanged += (_, _) => _library.Secrets.Set("export.xinzhi.token", xzTokenBox.Password);
        var xzExportBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentButtonStyle"], Content = L10n.T("导出") };
        xzExportBtn.Click += async (_, _) =>
        {
            try
            {
                var (b, m) = await _library.NoteExports.ExportToXinzhiAsync();
                _ownerWindow.ShowToast($"已导出 {b} 本书、{m} 条笔记到新枝");
            }
            catch (Exception ex) { _ownerWindow.ShowToast($"新枝导出失败：{ex.Message}"); }
        };
        xzPanel.Children.Add(xzTokenBox);
        xzPanel.Children.Add(xzExportBtn);
        PageBodyStack.Children.Add(new FluentSettingsCard(
            "新枝",
            "填入令牌后可将笔记增量发到新枝",
            "doc-lines",
            xzPanel,
            dark: dark,
            customIcon: ThemeHelper.CreateExportBrandTile("xinzhi", dark, 28)));
    }

    private void BuildStoragePage(bool dark)
    {
        PageBodyStack.Children.Add(MakeSectionHeader("已用空间", dark, first: true));

        var openDirBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentButtonStyle"], Content = L10n.T("在文件资源管理器中显示") };
        openDirBtn.Click += (_, _) => { try { Process.Start("explorer.exe", _library.DataDir); } catch { } };
        PageBodyStack.Children.Add(new FluentSettingsCard("书库位置", _library.DataDir, "drive", openDirBtn, dark: dark));

        var counts = _library.GetSnapshotCounts();
        string fileMb = $"{counts.FileBytes / (1024.0 * 1024.0):0.0} MB";
        string dataMb = $"{counts.DataBytes / (1024.0 * 1024.0):0.0} MB";
        PageBodyStack.Children.Add(new FluentSettingsCard(
            "书库统计",
            $"书 {counts.Books} 本（占用 {fileMb}）· 笔记 {counts.Notes} 条 · 生词 {counts.Words} 个 · 数据 {dataMb}",
            "book",
            dark: dark));

        var storageModelsCard = new FluentSettingsCard(
            "朗读模型与声音包",
            null,
            "wave",
            dark: dark);
        _storageModelsCard = storageModelsCard;
        UpdateVoiceModelCards();
        PageBodyStack.Children.Add(storageModelsCard);

        // Audio & temp cache cleanup (never touches books); deleted-book purge is a separate, confirmed action
        long cacheBytes = ComputeAudioCacheBytes();
        string cacheMb = $"{cacheBytes / (1024.0 * 1024.0):0.0} MB";
        var cleanBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentButtonStyle"], Content = L10n.T("清理缓存"), IsEnabled = cacheBytes > 0 };
        cleanBtn.Click += (_, _) =>
        {
            ClearAudioAndTempCaches();
            _ownerWindow.ShowToast("已清理朗读与临时音频缓存");
            RenderActivePage();
        };
        PageBodyStack.Children.Add(new FluentSettingsCard(
            "缓存与清理",
            cacheBytes > 0 ? $"当前占用 {cacheMb}（云端朗读缓存与广播剧临时片段，不影响书架里的书和笔记）" : "当前没有可清理的朗读缓存",
            "trash",
            cleanBtn,
            dark: dark));

        var purgeBtn = new Button
        {
            Style = (Style)Application.Current.Resources["YYFluentButtonStyle"],
            Content = L10n.T("彻底清空…"),
            Foreground = ThemeHelper.Brush(ThemeHelper.Palette(dark).Danger),
        };
        purgeBtn.Click += async (_, _) =>
        {
            bool ok = await YYChrome.ShowConfirmAsync(
                RootGrid.XamlRoot,
                dark,
                L10n.T("彻底清空最近删除？"),
                L10n.T("最近删除里的书文件会被永久移除，之后无法再从「最近删除」恢复。书架上的书和笔记不受影响。"),
                L10n.T("彻底清空"),
                L10n.T("取消"),
                destructive: true,
                tint: _library.Prefs.Tint);
            if (!ok || _disposed) return;
            uint purged = 0;
            try { purged = _library.PurgeDeleted(0); } catch { }
            _ownerWindow.ShowToast(purged > 0 ? $"已永久移除 {purged} 本已删书的文件" : "最近删除里没有需要清理的书");
            RenderActivePage();
        };
        PageBodyStack.Children.Add(new FluentSettingsCard(
            "最近删除",
            "永久移除已删书残留的文件以释放空间（不可恢复）",
            "trash",
            purgeBtn,
            dark: dark));
    }

    private long ComputeAudioCacheBytes()
    {
        long total = 0;
        foreach (string sub in new[] { "cloud-voices", "drama-live", "tts-cache", "tmp" })
        {
            string dir = Path.Combine(_library.DataDir, sub);
            if (!Directory.Exists(dir)) continue;
            try
            {
                foreach (var fi in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories))
                    total += fi.Length;
            }
            catch { }
        }
        return total;
    }

    private void ClearAudioAndTempCaches()
    {
        foreach (string sub in new[] { "cloud-voices", "drama-live", "tts-cache", "tmp" })
        {
            string dir = Path.Combine(_library.DataDir, sub);
            if (!Directory.Exists(dir)) continue;
            try
            {
                foreach (var fi in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    try { fi.Delete(); } catch { }
                }
            }
            catch { }
        }
    }

    private void BuildDataPage(bool dark)
    {
        PageBodyStack.Children.Add(MakeSectionHeader("生成快照", dark, first: true));

        var backupBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentAccentButtonStyle"], Content = L10n.T("生成快照") };
        backupBtn.Click += async (_, _) =>
        {
            string? path = await NativePickerHelper.PickSaveFileAsync(_ownerWindow, $"语阅快照-{DateTime.Now:yyyyMMdd-HHmmss}", [("语阅快照", [".yuyue"])]);
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                _library.MakeSnapshot(path, new SnapshotParts(Progress: true, Notes: true, Words: true, Settings: true, Files: true));
                _ownerWindow.ShowToast($"已保存快照：{Path.GetFileName(path)}");
            }
            catch (Exception ex) { _ownerWindow.ShowToast($"生成快照失败：{ex.Message}"); }
        };
        PageBodyStack.Children.Add(new FluentSettingsCard("生成快照", "打包全部书、进度、划线想法、生词与设置", "cube", backupBtn, dark: dark));

        var restoreBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentButtonStyle"], Content = L10n.T("导入快照…") };
        restoreBtn.Click += async (_, _) =>
        {
            if (RootGrid.XamlRoot is { } xr)
                await SnapshotAndConflictDialogs.ShowSnapshotImportDialogAsync(_ownerWindow, xr, _library);
        };
        PageBodyStack.Children.Add(new FluentSettingsCard("导入快照", "从 .yuyue 快照合并或覆盖当前书架", "restore-tab", restoreBtn, dark: dark));

        var csvBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentButtonStyle"], Content = L10n.T("导出…") };
        csvBtn.Click += async (_, _) =>
        {
            if (RootGrid.XamlRoot is { } xr)
                await SnapshotAndConflictDialogs.ShowReadingExportSheetAsync(_ownerWindow, xr, _library);
        };
        PageBodyStack.Children.Add(new FluentSettingsCard("导出阅读记录", "导出想读、在读、读完状态与完成日期（CSV），可导入 Goodreads 或 StoryGraph", "share-up", csvBtn, dark: dark));

        var histPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        bool canUndo = CanUndoSettings();
        var undoBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentButtonStyle"], Content = L10n.T("改回"), IsEnabled = canUndo };
        undoBtn.Click += (_, _) => PerformSettingsUndo();
        var redoBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentButtonStyle"], Content = L10n.T("还原"), IsEnabled = _library.SettingsHistory.CanRedo };
        redoBtn.Click += (_, _) => PerformSettingsRedo();
        var clearHistBtn = new Button
        {
            Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
            Content = L10n.T("清空"),
            IsEnabled = _library.SettingsHistory.Entries.Count > 0,
        };
        clearHistBtn.Click += (_, _) =>
        {
            _library.SettingsHistory.Clear();
            UpdateHistoryQuickButton();
            RenderActivePage();
            ShowSettingsSavedToast("已清空修改记录", showUndo: false);
        };
        histPanel.Children.Add(undoBtn);
        histPanel.Children.Add(redoBtn);
        histPanel.Children.Add(clearHistBtn);
        PageBodyStack.Children.Add(new FluentSettingsCard("最近改过", $"已记录 {_library.SettingsHistory.Entries.Count} 条近期改动（支持 Ctrl+Z 撤销）", "history", histPanel, dark: dark));

        if (_library.SettingsHistory.Entries.Count > 0)
        {
            PageBodyStack.Children.Add(BuildSettingsHistoryListPanel(dark, maxItems: 30));
        }
    }

    private void BuildAboutPage(bool dark)
    {
        var p = ThemeHelper.Palette(dark);
        var serif = new FontFamily(YYFontFamily.Serif.WindowsFamily());
        var latinSerif = new FontFamily(YYFontFamily.LatinSerif.WindowsFamily());
        var mono = new FontFamily(YYFontFamily.Mono.WindowsFamily());
        string ver = UpdateAndFeedbackService.CurrentVersion;
        ulong build = UpdateAndFeedbackService.CurrentBuild;
        bool preferCn = Yuyue.Speech.ModelDownload.PrefersChinaMirror();

        // Two-column Bookplate Spread (左：藏书票书扉 + Yuyue for Windows + 手写铅笔签名；右：Fluent 2 牌记卡片)
        var spread = new Grid { ColumnSpacing = 22, Margin = new Thickness(0, 4, 0, 8), MinHeight = 420 };
        spread.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(224) });
        spread.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Left Folio: Bookplate (书扉)
        var leftGrid = new Grid();
        leftGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        leftGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var leftTop = new StackPanel { Spacing = 0 };

        // 阅 seal mark
        var markInner = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        markInner.Children.Add(L10n.Skip(new TextBlock
        {
            Text = "阅",
            FontFamily = serif,
            FontSize = 29,
            FontWeight = FontWeights.Bold,
            Foreground = ThemeHelper.Brush(Windows.UI.Color.FromArgb(255, 28, 29, 32)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, -2, 0, 0),
        }));
        markInner.Children.Add(new Border
        {
            Width = 20,
            Height = 3,
            CornerRadius = new CornerRadius(1.5),
            Background = ThemeHelper.Brush(Windows.UI.Color.FromArgb(255, 43, 76, 126)),
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        var yuyueMark = new Border
        {
            Width = 58,
            Height = 58,
            CornerRadius = new CornerRadius(13),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = ThemeHelper.Brush(Colors.White),
            BorderBrush = ThemeHelper.Brush(Windows.UI.Color.FromArgb(20, 0, 0, 0)),
            BorderThickness = new Thickness(1),
            Child = markInner,
        };
        leftTop.Children.Add(yuyueMark);

        leftTop.Children.Add(new TextBlock
        {
            Text = L10n.T("语阅"),
            FontFamily = serif,
            FontSize = 26,
            CharacterSpacing = L10n.IsEnglish ? 25 : 130,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeHelper.Brush(p.T1),
            Margin = new Thickness(0, 14, 0, 3),
        });
        leftTop.Children.Add(L10n.Skip(new TextBlock
        {
            Text = L10n.IsEnglish ? "语阅 · for Windows" : "Yuyue for Windows",
            FontFamily = latinSerif,
            FontStyle = Windows.UI.Text.FontStyle.Italic,
            FontSize = 13,
            Foreground = ThemeHelper.Brush(p.T3),
        }));
        leftTop.Children.Add(L10n.Skip(new TextBlock
        {
            Text = $"v{ver} ({build})",
            FontFamily = mono,
            FontSize = 11,
            Foreground = ThemeHelper.Brush(p.T3),
            Margin = new Thickness(0, 4, 0, 0),
        }));
        leftTop.Children.Add(new Border
        {
            Width = 24,
            Height = 1.5,
            Background = ThemeHelper.Brush(p.Line),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 14, 0, 12),
        });
        leftTop.Children.Add(new TextBlock
        {
            Text = L10n.T("读着听，听着读，进度接得上。\n没有账号与广告，\n书与笔记都留在你手里。"),
            FontFamily = serif,
            FontSize = 13,
            LineHeight = 22,
            Foreground = ThemeHelper.Brush(p.T2),
            TextWrapping = TextWrapping.Wrap,
        });
        leftGrid.Children.Add(leftTop);

        // Graphite PencilSignature + by nowtin · GPL-3.0
        var (sigView, triggerSigWrite) = YYChrome.MakePencilSignature(58, dark, autoWrite: true);
        var sigWrap = new StackPanel
        {
            Spacing = 5,
            Margin = new Thickness(0, 20, 0, 0),
            Background = new SolidColorBrush(Colors.Transparent),
        };
        ToolTipService.SetToolTip(sigWrap, L10n.T("点击重绘手写签名"));
        sigWrap.PointerPressed += (_, _) => triggerSigWrite();
        sigWrap.Children.Add(sigView);

        var leftFoot = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            Background = new SolidColorBrush(Colors.Transparent),
            VerticalAlignment = VerticalAlignment.Center,
        };
        leftFoot.Children.Add(L10n.Skip(new TextBlock
        {
            Text = "by",
            FontSize = 11,
            Foreground = ThemeHelper.Brush(p.T3),
            VerticalAlignment = VerticalAlignment.Center,
        }));
        var authorText = L10n.Skip(new TextBlock
        {
            Text = "nowtin",
            FontSize = 11,
            FontWeight = FontWeights.Medium,
            Foreground = ThemeHelper.Brush(p.T1),
        });
        var authorBtn = new Button
        {
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(2, 0, 2, 0),
            MinWidth = 0,
            MinHeight = 0,
            Content = authorText,
            UseSystemFocusVisuals = false,
            VerticalAlignment = VerticalAlignment.Center,
        };
        authorBtn.PointerEntered += (_, _) => authorText.TextDecorations = Windows.UI.Text.TextDecorations.Underline;
        authorBtn.PointerExited += (_, _) => authorText.TextDecorations = Windows.UI.Text.TextDecorations.None;
        string authorUrl = preferCn ? "https://cnb.cool/inokoru" : "https://github.com/nowtin";
        authorBtn.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(authorUrl) { UseShellExecute = true }); } catch { } };
        leftFoot.Children.Add(authorBtn);
        leftFoot.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(5, 1, 5, 1),
            Background = ThemeHelper.Brush(p.Inset),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = L10n.Skip(new TextBlock { Text = "GPL-3.0", FontSize = 10, Foreground = ThemeHelper.Brush(p.T3) }),
        });
        sigWrap.Children.Add(leftFoot);

        Grid.SetRow(sigWrap, 1);
        leftGrid.Children.Add(sigWrap);

        var leftBookplateCard = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(18, 20, 18, 16),
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = leftGrid,
        };
        Grid.SetColumn(leftBookplateCard, 0);
        spread.Children.Add(leftBookplateCard);

        // Right Folio: Native Windows 11 Fluent Settings Cards
        var rightStack = new StackPanel { Spacing = 10, VerticalAlignment = VerticalAlignment.Top };

        var checkBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentAccentButtonStyle"], Content = L10n.T("检查更新") };
        checkBtn.Click += async (_, _) =>
        {
            if (RootGrid.XamlRoot is { } xr)
                await _library.Updates.ShowUpdateCheckDialogAsync(xr);
        };
        rightStack.Children.Add(new FluentSettingsCard("版本与更新", $"当前版本 {ver} ({build})", "info-circle", checkBtn, dark: dark));

        var fbBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentButtonStyle"], Content = L10n.T("写反馈…") };
        fbBtn.Click += async (_, _) =>
        {
            if (RootGrid.XamlRoot is { } xr)
                await _library.Updates.ShowFeedbackComposerAsync(_ownerWindow, xr);
        };
        rightStack.Children.Add(new FluentSettingsCard("问题与建议", "可附截图，无需注册账号", "feedback", fbBtn, dark: dark));

        var logBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentButtonStyle"], Content = L10n.T("查看") };
        logBtn.Click += async (_, _) =>
        {
            if (RootGrid.XamlRoot is { } xr)
                await _library.Updates.ShowChangelogSheetAsync(xr);
        };
        rightStack.Children.Add(new FluentSettingsCard("更新日志", "查看每个版本改了什么", "doc-lines", logBtn, dark: dark));

        var legalPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var privacyBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentButtonStyle"], Content = L10n.T("隐私说明") };
        privacyBtn.Click += async (_, _) =>
        {
            if (RootGrid.XamlRoot is { } xr)
                await UpdateAndFeedbackService.ShowPrivacySheetAsync(xr);
        };
        var licBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentButtonStyle"], Content = L10n.T("开源许可与致谢") };
        licBtn.Click += async (_, _) =>
        {
            if (RootGrid.XamlRoot is { } xr)
                await UpdateAndFeedbackService.ShowLicensesSheetAsync(xr);
        };
        legalPanel.Children.Add(privacyBtn);
        legalPanel.Children.Add(licBtn);
        rightStack.Children.Add(new FluentSettingsCard("隐私与开源", "书架与笔记完全保存在本地，查看隐私说明与开源许可", "doc-lines", legalPanel, dark: dark));

        var analyticsSw = new ToggleSwitch { IsOn = _library.Prefs.AnalyticsEnabled };
        analyticsSw.Toggled += (_, _) => _library.Analytics.SetEnabled(analyticsSw.IsOn);
        rightStack.Children.Add(new FluentSettingsCard("匿名使用统计", "仅记录功能使用形态，不关联身份，绝不收集书名、笔记或搜索词", "lock", analyticsSw, dark: dark));

        Grid.SetColumn(rightStack, 1);
        spread.Children.Add(rightStack);

        PageBodyStack.Children.Add(spread);
    }
}
