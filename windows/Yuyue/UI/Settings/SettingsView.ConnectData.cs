using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Yuyue.Core;
using Yuyue.Design;
using Yuyue.UI.Controls;

namespace Yuyue.UI.Settings;

public sealed partial class SettingsView
{
    private void BuildCompanionPage(bool dark)
    {
        PageBodyStack.Children.Add(MakeSectionHeader("AI 书伴模型接入（支持 18 家服务商与本地 Ollama）", dark, first: true));

        var provCombo = new ComboBox { Width = 220 };
        foreach (var pr in CompanionClient.Presets) provCombo.Items.Add(pr.Name);
        int pIdx = CompanionClient.Presets.ToList().FindIndex(x => string.Equals(x.Id, _library.Prefs.AiProvider, StringComparison.OrdinalIgnoreCase));
        provCombo.SelectedIndex = Math.Max(0, pIdx);
        provCombo.SelectionChanged += (_, _) =>
        {
            if (provCombo.SelectedIndex >= 0)
            {
                var chosen = CompanionClient.Presets[provCombo.SelectedIndex];
                _library.Prefs.AiProvider = chosen.Id;
                _library.Prefs.AiBaseUrl = _library.Prefs.AiProviderUrls.GetValueOrDefault(chosen.Id, chosen.BaseUrl);
                _library.Prefs.AiModel = _library.Prefs.AiProviderModels.GetValueOrDefault(chosen.Id, chosen.DefaultModel);
                _library.SavePrefs();
                RenderActivePage();
            }
        };
        PageBodyStack.Children.Add(new FluentSettingsCard("当前 AI 服务商", "API Key 使用 Windows DPAPI 本地加密保存，从不上传服务器", "sparkle", provCombo, dark: dark));

        var keyBox = new PasswordBox
        {
            Style = (Style)Application.Current.Resources["YYCompactPasswordBoxStyle"],
            Width = 260,
            Password = _library.Secrets.Get($"ai.key.{_library.Prefs.AiProvider}") ?? "",
            PlaceholderText = "sk-…",
        };
        keyBox.PasswordChanged += (_, _) =>
        {
            _library.Secrets.Set($"ai.key.{_library.Prefs.AiProvider}", keyBox.Password);
            if (!string.IsNullOrEmpty(_library.Secrets.Get("ai.apiKey")))
                _library.Secrets.Set("ai.apiKey", "");
        };
        PageBodyStack.Children.Add(new FluentSettingsCard("API 密钥 (Key)", "本地 Ollama / LM Studio 可留空", "lock", keyBox, dark: dark));

        var urlBox = new TextBox { Style = (Style)Application.Current.Resources["YYSingleLineTextBoxStyle"], Width = 260, Text = _library.Prefs.AiBaseUrl };
        urlBox.TextChanged += (_, _) =>
        {
            _library.Prefs.AiBaseUrl = urlBox.Text ?? "";
            _library.Prefs.AiProviderUrls[_library.Prefs.AiProvider] = _library.Prefs.AiBaseUrl;
            _library.SavePrefs();
        };
        PageBodyStack.Children.Add(new FluentSettingsCard("接口地址 (Base URL)", "兼容 OpenAI / Anthropic / Gemini 协议", "globe", urlBox, dark: dark));

        var modelBox = new TextBox { Style = (Style)Application.Current.Resources["YYSingleLineTextBoxStyle"], Width = 260, Text = _library.Prefs.AiModel };
        modelBox.TextChanged += (_, _) =>
        {
            _library.Prefs.AiModel = modelBox.Text ?? "";
            _library.Prefs.AiProviderModels[_library.Prefs.AiProvider] = _library.Prefs.AiModel;
            _library.SavePrefs();
        };
        PageBodyStack.Children.Add(new FluentSettingsCard("模型名称 (Model)", "如 deepseek-chat / claude-sonnet-4-5 / qwen2.5:7b", "bubble-lines", modelBox, dark: dark));

        var testBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentAccentButtonStyle"], Content = L10n.T("测试连接") };
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
        PageBodyStack.Children.Add(new FluentSettingsCard("连接状态验证", "发送一次极简握手确认密钥与模型可用", "check-circle", testBtn, dark: dark));

        // AI Usage statistics card
        var today = _library.Usage.GetToday();
        var snap30 = _library.Usage.Snapshot30Days();
        PageBodyStack.Children.Add(MakeSectionHeader("AI 用量统计（本地记录）", dark));
        PageBodyStack.Children.Add(new FluentSettingsCard(
            "近 30 天与今日用量",
            $"今日 {today.Requests} 次请求（{CompanionUsageLedger.FormatCount(today.TotalTokens)} tokens）· 近 30 天累计 {CompanionUsageLedger.FormatCount(snap30.TotalTokens)} tokens",
            "clock",
            dark: dark));
    }

    private void BuildSyncPage(bool dark)
    {
        PageBodyStack.Children.Add(MakeSectionHeader("跨设备同步（自有网盘 · 零账号）", dark, first: true));

        var methodCombo = new ComboBox { Width = 260 };
        (string Key, string Lbl)[] methods = [("none", "关闭同步"), ("webdav", "WebDAV（坚果云 / Nextcloud）"), ("folder", "本地同步目录（OneDrive / iCloud）"), ("gdrive", "Google Drive")];
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
            PageBodyStack.Children.Add(new FluentSettingsCard("WebDAV 服务器地址", "坚果云默认：https://dav.jianguoyun.com/dav/", "server", davUrl, dark: dark));

            var davUser = new TextBox { Style = (Style)Application.Current.Resources["YYSingleLineTextBoxStyle"], Width = 260, Text = _library.Prefs.WebDavUser };
            davUser.TextChanged += (_, _) => { _library.Prefs.WebDavUser = davUser.Text ?? ""; _library.SavePrefs(); };
            PageBodyStack.Children.Add(new FluentSettingsCard("账号 / 邮箱", null, "pencil", davUser, dark: dark));

            var davPwd = new PasswordBox { Style = (Style)Application.Current.Resources["YYCompactPasswordBoxStyle"], Width = 260, Password = _library.Secrets.Get("sync.webdav.password") ?? "" };
            davPwd.PasswordChanged += (_, _) => _library.Secrets.Set("sync.webdav.password", davPwd.Password);
            PageBodyStack.Children.Add(new FluentSettingsCard("应用专用密码", "DPAPI 加密存储在本地凭据库", "lock", davPwd, dark: dark));

            var checkDavBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentButtonStyle"], Content = L10n.T("一键连通性诊断") };
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
                    if (!_disposed) _ownerWindow.ShowToast($"WebDAV 诊断失败：{ex.Message}");
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
                box.Children.Add(Design.YYChrome.MakeDialogHeader("WebDAV 连通性诊断", report.AllPassed ? "全部检查通过" : "发现连接问题", () => dlgRef?.Hide(), dark));
                box.Children.Add(WebDavCheckService.BuildCheckCard(report, p, tc));
                dlgRef = Design.YYChrome.CreateFluentDialog(xr, dark, 480, box, tint: _library.Prefs.Tint);
                await dlgRef.ShowSafeAsync(_library.Prefs.Tint);
            };
            PageBodyStack.Children.Add(new FluentSettingsCard("WebDAV 连接诊断", "逐项检测地址、认证、目录权限与读写耗时", "check-circle", checkDavBtn, dark: dark));
        }
        else if (_library.Prefs.SyncMethod == "folder")
        {
            var pickBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentButtonStyle"], Content = L10n.T("选择同步文件夹…") };
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
                "本地同步文件夹",
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
            PageBodyStack.Children.Add(new FluentSettingsCard("手动触发同步", "同步阅读进度、划线笔记、生词与书架", "cloud", syncNowBtn, dark: dark));

            var conflictBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentButtonStyle"], Content = L10n.T("检查笔记冲突…") };
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
            PageBodyStack.Children.Add(new FluentSettingsCard("多端笔记冲突解决", "若两台设备同时修改同一条笔记，可在此对比保留", "checklist-lines", conflictBtn, dark: dark));
        }

        // Connected devices list
        var devices = _library.GetDevices();
        if (devices.Count > 0)
        {
            PageBodyStack.Children.Add(MakeSectionHeader($"已同步设备 ({devices.Count})", dark));
            foreach (var dev in devices)
            {
                string subtitle = dev.ThisDevice ? "当前这台电脑" : $"{dev.Platform}";
                if (!string.IsNullOrWhiteSpace(dev.LastBook))
                    subtitle += $" · 最近在读《{dev.LastBook}》";
                PageBodyStack.Children.Add(new FluentSettingsCard(dev.Name, subtitle, "laptop", dark: dark));
            }
        }
    }

    private void BuildExportPage(bool dark)
    {
        PageBodyStack.Children.Add(MakeSectionHeader("笔记与知识库导出", dark, first: true));

        var obsPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var obsPickBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentButtonStyle"], Content = L10n.T("选择库目录…") };
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
        var obsExportBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentAccentButtonStyle"], Content = L10n.T("立即导出") };
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
            "Obsidian 本地 Markdown 库",
            string.IsNullOrWhiteSpace(_library.Prefs.ObsidianFolder) ? "尚未选择 Obsidian 文件夹" : _library.Prefs.ObsidianFolder,
            "obsidian",
            obsPanel,
            dark: dark));

        // flomo webhook + test export
        string curFlomo = _library.Secrets.Get("export.flomo.url");
        if (string.IsNullOrWhiteSpace(curFlomo)) curFlomo = _library.Prefs.FlomoWebhookUrl ?? "";
        var flomoPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var flomoBox = new TextBox
        {
            Style = (Style)Application.Current.Resources["YYSingleLineTextBoxStyle"],
            Width = 220,
            Text = curFlomo,
            PlaceholderText = "https://flomoapp.com/iwh/…",
        };
        flomoBox.TextChanged += (_, _) =>
        {
            string v = (flomoBox.Text ?? "").Trim();
            _library.Prefs.FlomoWebhookUrl = v;
            _library.Secrets.Set("export.flomo.url", v);
            _library.SavePrefs();
        };
        var flomoExportBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentButtonStyle"], Content = L10n.T("推送") };
        flomoExportBtn.Click += async (_, _) =>
        {
            if (!NoteExportService.ValidateFlomoWebhook((flomoBox.Text ?? "").Trim()))
            {
                _ownerWindow.ShowToast("请先输入有效的 flomo Webhook 地址（以 https://flomoapp.com/iwh/ 开头）");
                return;
            }
            try
            {
                var (b, m) = await _library.NoteExports.ExportToFlomoAsync();
                _ownerWindow.ShowToast($"已推送 {b} 本书、{m} 条笔记到 flomo");
            }
            catch (Exception ex) { _ownerWindow.ShowToast($"flomo 推送失败：{ex.Message}"); }
        };
        flomoPanel.Children.Add(flomoBox);
        flomoPanel.Children.Add(flomoExportBtn);
        PageBodyStack.Children.Add(new FluentSettingsCard("flomo 专属 Webhook", "增量去重同步划线与批注想法到 flomo", "flomo", flomoPanel, dark: dark));

        // Notion integration
        var notionExp = new FluentSettingsExpander(
            "Notion 知识库导出",
            string.IsNullOrWhiteSpace(_library.Prefs.NotionPageTitle) ? "填写 Integration Token 与目标页面链接后可一键导出" : $"已绑定页面：{_library.Prefs.NotionPageTitle}",
            "notion",
            dark: dark);
        var notionTokenBox = new PasswordBox
        {
            Style = (Style)Application.Current.Resources["YYCompactPasswordBoxStyle"],
            Width = 240,
            Password = _library.Secrets.Get("export.notion.token") ?? "",
            PlaceholderText = "ntn_… / secret_…",
        };
        notionTokenBox.PasswordChanged += (_, _) => _library.Secrets.Set("export.notion.token", notionTokenBox.Password);
        notionExp.AddKeyValueRow("Integration Token", "在 Notion Integrations 中创建并授权给目标页面", notionTokenBox);

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
        notionExp.AddKeyValueRow("目标父页面", "每本书会自动在该页面下生成独立子页", notionPageBox);

        var notionBtns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var notionVerifyBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentButtonStyle"], Content = L10n.T("验证连接") };
        notionVerifyBtn.Click += async (_, _) =>
        {
            var (ok, title, msg) = await _library.NoteExports.VerifyNotionAsync(notionTokenBox.Password, notionPageBox.Text ?? "");
            if (ok) { _library.Prefs.NotionPageTitle = title; _library.SavePrefs(); }
            _ownerWindow.ShowToast(ok ? $"Notion 验证成功：{title}" : $"Notion 验证失败：{msg}");
        };
        var notionSyncBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentAccentButtonStyle"], Content = L10n.T("立即导出") };
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
        notionExp.AddKeyValueRow("连接与导出", "支持断点增量同步，避免重复写入", notionBtns);
        PageBodyStack.Children.Add(notionExp);

        // Xinzhi integration
        var xzPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var xzTokenBox = new PasswordBox
        {
            Style = (Style)Application.Current.Resources["YYCompactPasswordBoxStyle"],
            Width = 210,
            Password = _library.Secrets.Get("export.xinzhi.token") ?? "",
            PlaceholderText = "新枝 CLI Token",
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
        PageBodyStack.Children.Add(new FluentSettingsCard("新枝 (Xinzhi) 同步", "配置 Token 后可将笔记增量推送至新枝", "sparkle", xzPanel, dark: dark));
    }

    private void BuildStoragePage(bool dark)
    {
        PageBodyStack.Children.Add(MakeSectionHeader("本地数据与存储空间", dark, first: true));

        var openDirBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentButtonStyle"], Content = L10n.T("在资源管理器中打开") };
        openDirBtn.Click += (_, _) => { try { Process.Start("explorer.exe", _library.DataDir); } catch { } };
        PageBodyStack.Children.Add(new FluentSettingsCard("书库数据目录", _library.DataDir, "drive", openDirBtn, dark: dark));

        var counts = _library.GetSnapshotCounts();
        string fileMb = $"{counts.FileBytes / (1024.0 * 1024.0):0.0} MB";
        string dataMb = $"{counts.DataBytes / (1024.0 * 1024.0):0.0} MB";
        PageBodyStack.Children.Add(new FluentSettingsCard(
            "本地藏书与笔记统计",
            $"书籍 {counts.Books} 本（占用 {fileMb}）· 划线笔记 {counts.Notes} 条 · 生词 {counts.Words} 个 · 数据 {dataMb}",
            "book",
            dark: dark));

        var storageModelsCard = new FluentSettingsCard(
            "离线模型与扩展包",
            null,
            "wave",
            dark: dark);
        _storageModelsCard = storageModelsCard;
        UpdateVoiceModelCards();
        PageBodyStack.Children.Add(storageModelsCard);
    }

    private void BuildDataPage(bool dark)
    {
        PageBodyStack.Children.Add(MakeSectionHeader("完整快照备份与迁移", dark, first: true));

        var backupBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentAccentButtonStyle"], Content = L10n.T("生成 .yuyue 备份…") };
        backupBtn.Click += async (_, _) =>
        {
            string? path = await NativePickerHelper.PickSaveFileAsync(_ownerWindow, $"yuyue-backup-{DateTime.Now:yyyyMMdd}", [("语阅备份快照", [".yuyue"])]);
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                _library.MakeSnapshot(path, new SnapshotParts(Progress: true, Notes: true, Words: true, Settings: true, Files: true));
                _ownerWindow.ShowToast($"已保存快照：{Path.GetFileName(path)}");
            }
            catch (Exception ex) { _ownerWindow.ShowToast($"备份失败：{ex.Message}"); }
        };
        PageBodyStack.Children.Add(new FluentSettingsCard("打包导出完整快照", "包含全部电子书、阅读进度、划线想法、生词与偏好设置", "cube", backupBtn, dark: dark));

        var restoreBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentButtonStyle"], Content = L10n.T("从快照恢复…") };
        restoreBtn.Click += async (_, _) =>
        {
            if (RootGrid.XamlRoot is { } xr)
                await SnapshotAndConflictDialogs.ShowSnapshotImportDialogAsync(_ownerWindow, xr, _library);
        };
        PageBodyStack.Children.Add(new FluentSettingsCard("从 .yuyue 快照恢复", "支持合并或完整替换当前书库", "restore-tab", restoreBtn, dark: dark));

        var csvBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentButtonStyle"], Content = L10n.T("导出阅读记录 CSV…") };
        csvBtn.Click += async (_, _) =>
        {
            if (RootGrid.XamlRoot is { } xr)
                await SnapshotAndConflictDialogs.ShowReadingExportSheetAsync(_ownerWindow, xr, _library);
        };
        PageBodyStack.Children.Add(new FluentSettingsCard("导出阅读统计与书单", "以标准 CSV 表格导出每本书的阅读时长与进度", "share-up", csvBtn, dark: dark));

        if (_library.SettingsHistory.Entries.Count > 0)
        {
            var histPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            bool canUndo = _library.SettingsHistory.Entries.Any(e => !e.Undone && _library.SettingsHistory.CanActuate(e, applyUndo: true));
            var undoBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentButtonStyle"], Content = L10n.T("撤销上次改动"), IsEnabled = canUndo };
            undoBtn.Click += (_, _) =>
            {
                if (_library.SettingsHistory.TryUndo() is not null)
                {
                    BroadcastReaderSettingsWithoutSelfRefresh();
                    App.CurrentApp.BroadcastAppearanceChanged();
                }
            };
            var redoBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentButtonStyle"], Content = L10n.T("重做"), IsEnabled = _library.SettingsHistory.CanRedo };
            redoBtn.Click += (_, _) =>
            {
                if (_library.SettingsHistory.TryRedo() is not null)
                {
                    BroadcastReaderSettingsWithoutSelfRefresh();
                    App.CurrentApp.BroadcastAppearanceChanged();
                }
            };
            histPanel.Children.Add(undoBtn);
            histPanel.Children.Add(redoBtn);
            PageBodyStack.Children.Add(new FluentSettingsCard("设置变更历史", $"已记录 {_library.SettingsHistory.Entries.Count} 条近期改动", "history", histPanel, dark: dark));
        }
    }

    private void BuildAboutPage(bool dark)
    {
        PageBodyStack.Children.Add(MakeSectionHeader("语阅 · Windows 11 原生版", dark, first: true));

        string ver = UpdateAndFeedbackService.CurrentVersion;
        ulong build = UpdateAndFeedbackService.CurrentBuild;
        var checkBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentAccentButtonStyle"], Content = L10n.T("检查更新") };
        checkBtn.Click += async (_, _) =>
        {
            if (RootGrid.XamlRoot is { } xr)
                await _library.Updates.ShowUpdateCheckDialogAsync(xr);
        };
        PageBodyStack.Children.Add(new FluentSettingsCard($"语阅 Yuyue v{ver} (Build {build})", "本地优先 · 无账号 · 看、听、记对着同一句话", "info-circle", checkBtn, dark: dark));

        var fbBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentButtonStyle"], Content = L10n.T("提交反馈") };
        fbBtn.Click += async (_, _) =>
        {
            if (RootGrid.XamlRoot is { } xr)
                await _library.Updates.ShowFeedbackComposerAsync(_ownerWindow, xr);
        };
        PageBodyStack.Children.Add(new FluentSettingsCard("问题反馈与功能建议", "附带系统环境与错误日志帮助我们更快修复", "feedback", fbBtn, dark: dark));

        var logBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentButtonStyle"], Content = L10n.T("查看更新日志") };
        logBtn.Click += async (_, _) =>
        {
            if (RootGrid.XamlRoot is { } xr)
                await _library.Updates.ShowChangelogSheetAsync(xr);
        };
        PageBodyStack.Children.Add(new FluentSettingsCard("版本更新日志", "了解近期 Windows 11 原生交互与功能改进", "doc-lines", logBtn, dark: dark));

        var legalPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var privacyBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentButtonStyle"], Content = L10n.T("隐私政策") };
        privacyBtn.Click += async (_, _) =>
        {
            if (RootGrid.XamlRoot is { } xr)
                await UpdateAndFeedbackService.ShowPrivacySheetAsync(xr);
        };
        var licBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentButtonStyle"], Content = L10n.T("开源许可") };
        licBtn.Click += async (_, _) =>
        {
            if (RootGrid.XamlRoot is { } xr)
                await UpdateAndFeedbackService.ShowLicensesSheetAsync(xr);
        };
        legalPanel.Children.Add(privacyBtn);
        legalPanel.Children.Add(licBtn);
        PageBodyStack.Children.Add(new FluentSettingsCard("隐私与开源声明", "书库与笔记完全保存在本地，查看隐私说明与第三方开源组件许可", "doc-lines", legalPanel, dark: dark));

        var analyticsSw = new ToggleSwitch { IsOn = _library.Prefs.AnalyticsEnabled };
        analyticsSw.Toggled += (_, _) => _library.Analytics.SetEnabled(analyticsSw.IsOn);
        PageBodyStack.Children.Add(new FluentSettingsCard("匿名使用统计", "仅收集不含书名与个人内容的匿名功能使用频次，可随时关闭", "lock", analyticsSw, dark: dark));
    }
}
