using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Windows.System;
using Windows.UI;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Yuyue.Design;

namespace Yuyue.Core;

public enum WebDavStepState
{
    Ok,
    Warn,
    Bad,
    Running,
    Untested,
}

public sealed record WebDavCheckStep(
    string Title,
    WebDavStepState State,
    string Detail,
    string? FixHint = null,
    string? FixLinkLabel = null,
    string? FixLinkUrl = null);

public sealed record WebDavCheckReport(
    bool AllPassed,
    double ElapsedSeconds,
    IReadOnlyList<WebDavCheckStep> Steps);

/// <summary>
/// 1:1 port of <c>apple/Yuyue/App/WebDAVCheck.swift</c> + <c>WebDAVCheckCard.swift</c>:
/// Sequential 4-step diagnostic (<c>能连上服务器</c> → <c>证书有效</c> → <c>账号和密码对</c> → <c>文件夹能读写</c>),
/// streaming live step transitions via <c>onProgress</c>, stopping at the first failing step while leaving
/// remaining steps as <c>Untested</c>, disposing TLS certificates deterministically, and distinguishing
/// user cancellation from network timeouts.
/// </summary>
public static class WebDavCheckService
{
    public static async Task<WebDavCheckReport> RunAsync(
        string rawUrl,
        string username,
        string password,
        string rawFolder,
        CancellationToken ct = default,
        Action<IReadOnlyList<WebDavCheckStep>>? onProgress = null)
    {
        var sw = Stopwatch.StartNew();
        var steps = new WebDavCheckStep[4]
        {
            new("能连上服务器", WebDavStepState.Untested, "没测…"),
            new("证书有效", WebDavStepState.Untested, "没测…"),
            new("账号和密码对", WebDavStepState.Untested, "没测…"),
            new("文件夹能读写", WebDavStepState.Untested, "没测…"),
        };

        void Emit() => onProgress?.Invoke((WebDavCheckStep[])steps.Clone());

        string url = rawUrl.Trim();
        if (!url.EndsWith('/')) url += "/";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != "https" && baseUri.Scheme != "http"))
        {
            steps[0] = new("能连上服务器", WebDavStepState.Bad, "地址格式不正确", "请填写完整的 https:// 或 http:// WebDAV 服务器地址。");
            steps[1] = steps[1] with { Detail = "没测，要先连上" };
            steps[2] = steps[2] with { Detail = "没测，要先连上" };
            steps[3] = steps[3] with { Detail = "没测，要先连上" };
            Emit();
            return new WebDavCheckReport(false, sw.Elapsed.TotalSeconds, steps);
        }

        X509Certificate2? leafCert = null;
        System.Net.Security.SslPolicyErrors sslErrors = System.Net.Security.SslPolicyErrors.None;

        try
        {
            using var handler = new SocketsHttpHandler
            {
                ConnectTimeout = System.TimeSpan.FromSeconds(15),
                SslOptions = new System.Net.Security.SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = (_, cert, _, errors) =>
                    {
                        leafCert?.Dispose();
                        leafCert = cert is not null ? new X509Certificate2(cert) : null;
                        sslErrors = errors;
                        return errors == System.Net.Security.SslPolicyErrors.None;
                    },
                },
            };
            using var client = new HttpClient(handler) { Timeout = System.TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Yuyue/0.1");

            // Step 1 & 2: OPTIONS + TLS Certificate
            steps[0] = new("能连上服务器", WebDavStepState.Running, "正在连接…");
            Emit();

            var stepSw = Stopwatch.StartNew();
            try
            {
                using var optReq = new HttpRequestMessage(HttpMethod.Options, baseUri);
                using var optResp = await client.SendAsync(optReq, ct);
                steps[0] = new("能连上服务器", WebDavStepState.Ok, $"{Math.Max(1, stepSw.ElapsedMilliseconds)} ms");
                Emit();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                steps[0] = new("能连上服务器", WebDavStepState.Untested, "已取消");
                Emit();
                throw;
            }
            catch (HttpRequestException) when (sslErrors != System.Net.Security.SslPolicyErrors.None)
            {
                steps[0] = new("能连上服务器", WebDavStepState.Ok, $"{Math.Max(1, stepSw.ElapsedMilliseconds)} ms");
                steps[1] = new(
                    "证书有效",
                    WebDavStepState.Bad,
                    "证书不受信任",
                    "服务器的 HTTPS 证书过期或为自签名证书；建议使用 Let's Encrypt 等受系统信任的证书。");
                steps[2] = steps[2] with { Detail = "没测，要先连上" };
                steps[3] = steps[3] with { Detail = "没测，要先连上" };
                Emit();
                return new WebDavCheckReport(false, sw.Elapsed.TotalSeconds, steps);
            }
            catch (TaskCanceledException)
            {
                steps[0] = new("能连上服务器", WebDavStepState.Bad, "连接超时", "15 秒内没有收到响应，请检查服务器地址、端口或网络连接。");
                steps[1] = steps[1] with { Detail = "没测，要先连上" };
                steps[2] = steps[2] with { Detail = "没测，要先连上" };
                steps[3] = steps[3] with { Detail = "没测，要先连上" };
                Emit();
                return new WebDavCheckReport(false, sw.Elapsed.TotalSeconds, steps);
            }
            catch (Exception ex)
            {
                steps[0] = new("能连上服务器", WebDavStepState.Bad, "无法连接", $"未能连上 {baseUri.Host}：{ex.Message}");
                steps[1] = steps[1] with { Detail = "没测，要先连上" };
                steps[2] = steps[2] with { Detail = "没测，要先连上" };
                steps[3] = steps[3] with { Detail = "没测，要先连上" };
                Emit();
                return new WebDavCheckReport(false, sw.Elapsed.TotalSeconds, steps);
            }

            if (baseUri.Scheme == "http")
            {
                steps[1] = new("证书有效", WebDavStepState.Warn, "没有加密 · http，密码明着传");
            }
            else
            {
                string expiry = leafCert is not null
                    ? $"到 {leafCert.NotAfter:yyyy 年 M 月}"
                    : "已验证";
                steps[1] = new("证书有效", WebDavStepState.Ok, expiry);
            }
            steps[2] = new("账号和密码对", WebDavStepState.Running, "正在验证…");
            Emit();

            // Prepare Basic Auth
            string authB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));

            // Step 3: PROPFIND Depth: 0
            try
            {
                using var propReq = new HttpRequestMessage(new HttpMethod("PROPFIND"), baseUri);
                propReq.Headers.Authorization = new AuthenticationHeaderValue("Basic", authB64);
                propReq.Headers.Add("Depth", "0");
                using var propResp = await client.SendAsync(propReq, ct);
                int code = (int)propResp.StatusCode;

                if (code is 401 or 403)
                {
                    bool jgy = baseUri.Host.Contains("jianguoyun", StringComparison.OrdinalIgnoreCase);
                    steps[2] = new(
                        "账号和密码对",
                        WebDavStepState.Bad,
                        $"认证被拒绝 ({code})",
                        jgy
                            ? "坚果云第三方 WebDAV 必须使用「应用密码」，不能直接填写登录密码。"
                            : "请检查用户名与密码；若开启了两步验证，请生成专用「应用密码」。",
                        jgy ? "打开坚果云安全设置 ↗" : null,
                        jgy ? "https://www.jianguoyun.com/d/account#safe" : null);
                    steps[3] = steps[3] with { Detail = "没测，要先登录" };
                    Emit();
                    return new WebDavCheckReport(false, sw.Elapsed.TotalSeconds, steps);
                }
                if (code is 404 or 405)
                {
                    steps[2] = new(
                        "账号和密码对",
                        WebDavStepState.Bad,
                        $"路径不是 WebDAV 入口 ({code})",
                        "请确认地址末尾包含正确的 WebDAV 根路径（如坚果云为 /dav/，Nextcloud 为 /remote.php/dav/files/用户名/）。");
                    steps[3] = steps[3] with { Detail = "没测，要先登录" };
                    Emit();
                    return new WebDavCheckReport(false, sw.Elapsed.TotalSeconds, steps);
                }
                if (code >= 400)
                {
                    steps[2] = new("账号和密码对", WebDavStepState.Bad, $"服务器返回 HTTP {code}", "请检查服务器 WebDAV 服务是否正常运行。");
                    steps[3] = steps[3] with { Detail = "没测，要先登录" };
                    Emit();
                    return new WebDavCheckReport(false, sw.Elapsed.TotalSeconds, steps);
                }

                steps[2] = new("账号和密码对", WebDavStepState.Ok, "已通过");
                steps[3] = new("文件夹能读写", WebDavStepState.Running, "正在测试读写…");
                Emit();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                steps[2] = new("账号和密码对", WebDavStepState.Untested, "已取消");
                Emit();
                throw;
            }
            catch (TaskCanceledException)
            {
                steps[2] = new("账号和密码对", WebDavStepState.Bad, "验证超时", "服务器在验证账号时响应超时，请稍后再试。");
                steps[3] = steps[3] with { Detail = "没测，要先登录" };
                Emit();
                return new WebDavCheckReport(false, sw.Elapsed.TotalSeconds, steps);
            }
            catch (Exception ex)
            {
                steps[2] = new("账号和密码对", WebDavStepState.Bad, "校验出错", ex.Message);
                steps[3] = steps[3] with { Detail = "没测，要先登录" };
                Emit();
                return new WebDavCheckReport(false, sw.Elapsed.TotalSeconds, steps);
            }

            // Step 4: Nested segment-by-segment MKCOL + PUT + DELETE
            string folderName = string.IsNullOrWhiteSpace(rawFolder) ? "语阅" : rawFolder.Trim().Replace('\\', '/').Trim('/');
            string[] segs = folderName.Split('/', StringSplitOptions.RemoveEmptyEntries);
            string escapedRel = string.Join("/", segs.Select(Uri.EscapeDataString));
            var folderUri = new Uri(baseUri, escapedRel + "/");
            string probeFile = $".yuyue-check-{Guid.NewGuid().ToString("N")[..8]}";
            var probeUri = new Uri(folderUri, probeFile);

            try
            {
                string acc = "";
                foreach (string seg in segs)
                {
                    acc = string.IsNullOrEmpty(acc) ? Uri.EscapeDataString(seg) : $"{acc}/{Uri.EscapeDataString(seg)}";
                    var segUri = new Uri(baseUri, acc + "/");
                    using var mkReq = new HttpRequestMessage(new HttpMethod("MKCOL"), segUri);
                    mkReq.Headers.Authorization = new AuthenticationHeaderValue("Basic", authB64);
                    using var mkResp = await client.SendAsync(mkReq, ct);
                    int mkCode = (int)mkResp.StatusCode;
                    if (mkCode == 507)
                    {
                        steps[3] = new("文件夹能读写", WebDavStepState.Bad, "网盘满了 (507)", "云盘剩余空间不足，请清理后再开启同步。");
                        Emit();
                        return new WebDavCheckReport(false, sw.Elapsed.TotalSeconds, steps);
                    }
                    if (mkCode == 409)
                    {
                        steps[3] = new("文件夹能读写", WebDavStepState.Bad, "上级路径不存在 (409)", $"无法创建目录「/{folderName}/」，请检查文件夹层级。");
                        Emit();
                        return new WebDavCheckReport(false, sw.Elapsed.TotalSeconds, steps);
                    }
                    if (mkCode >= 500)
                    {
                        steps[3] = new("文件夹能读写", WebDavStepState.Bad, $"服务器异常 ({mkCode})", "WebDAV 服务器返回 5xx 错误，请稍后重试。");
                        Emit();
                        return new WebDavCheckReport(false, sw.Elapsed.TotalSeconds, steps);
                    }
                }

                using (var putReq = new HttpRequestMessage(HttpMethod.Put, probeUri))
                {
                    putReq.Headers.Authorization = new AuthenticationHeaderValue("Basic", authB64);
                    putReq.Content = new ByteArrayContent("ok"u8.ToArray());
                    using var putResp = await client.SendAsync(putReq, ct);
                    int pCode = (int)putResp.StatusCode;
                    if (pCode == 507)
                    {
                        steps[3] = new("文件夹能读写", WebDavStepState.Bad, "网盘满了 (507)", "云盘剩余空间不足，请清理后再试。");
                        Emit();
                        return new WebDavCheckReport(false, sw.Elapsed.TotalSeconds, steps);
                    }
                    if (pCode < 200 || pCode >= 300)
                    {
                        steps[3] = new("文件夹能读写", WebDavStepState.Bad, $"无法写入 ({pCode})", $"账号对「/{folderName}/」没有写入权限，请在网盘后台开启读写权限或先手动创建该目录。");
                        Emit();
                        return new WebDavCheckReport(false, sw.Elapsed.TotalSeconds, steps);
                    }
                }

                using (var delReq = new HttpRequestMessage(HttpMethod.Delete, probeUri))
                {
                    delReq.Headers.Authorization = new AuthenticationHeaderValue("Basic", authB64);
                    using var _ = await client.SendAsync(delReq, ct);
                }

                steps[3] = new("文件夹能读写", WebDavStepState.Ok, $"/{folderName}/ 可正常读写");
                Emit();
                return new WebDavCheckReport(true, sw.Elapsed.TotalSeconds, steps);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                steps[3] = new("文件夹能读写", WebDavStepState.Untested, "已取消");
                Emit();
                throw;
            }
            catch (TaskCanceledException)
            {
                steps[3] = new("文件夹能读写", WebDavStepState.Bad, "读写测试超时", "写入探测文件超时，请检查网络连接或服务器负载。");
                Emit();
                return new WebDavCheckReport(false, sw.Elapsed.TotalSeconds, steps);
            }
            catch (Exception ex)
            {
                steps[3] = new("文件夹能读写", WebDavStepState.Bad, "读写测试失败", ex.Message);
                Emit();
                return new WebDavCheckReport(false, sw.Elapsed.TotalSeconds, steps);
            }
        }
        finally
        {
            leafCert?.Dispose();
        }
    }

    public static UIElement BuildCheckCard(WebDavCheckReport report, YYPalette p, YYTintColors tint) =>
        BuildCheckCard(report.Steps, p, tint, isRunning: false, allPassed: report.AllPassed, elapsedSeconds: report.ElapsedSeconds);

    public static UIElement BuildCheckCard(
        IReadOnlyList<WebDavCheckStep> steps,
        YYPalette p,
        YYTintColors tint,
        bool isRunning = true,
        bool allPassed = false,
        double elapsedSeconds = 0)
    {
        var outer = new StackPanel { Spacing = 10 };

        var head = new Grid();
        string headline = isRunning
            ? "正在检查连接…"
            : (allPassed ? "连接检查通过" : "连接检查未通过");
        Color headFg = isRunning
            ? p.T1
            : (allPassed ? p.Ok : p.Danger);
        head.Children.Add(new TextBlock
        {
            Text = headline,
            FontSize = 12.5,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = ThemeHelper.Brush(headFg),
        });
        if (!isRunning)
        {
            head.Children.Add(new TextBlock
            {
                Text = $"刚刚 · 用了 {elapsedSeconds:0.0} 秒",
                FontSize = 11,
                Foreground = ThemeHelper.Brush(p.T3),
                HorizontalAlignment = HorizontalAlignment.Right,
            });
        }
        outer.Children.Add(head);

        var timeline = new StackPanel { Spacing = 0 };
        for (int i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            bool isLast = i == steps.Count - 1;

            var rowGrid = new Grid { ColumnSpacing = 10 };
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var leftCol = new Grid();
            leftCol.RowDefinitions.Add(new RowDefinition { Height = new GridLength(20) });
            leftCol.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            Color badgeBg = s.State switch
            {
                WebDavStepState.Ok => Color.FromArgb(32, p.Ok.R, p.Ok.G, p.Ok.B),
                WebDavStepState.Warn => Color.FromArgb(38, 210, 145, 32),
                WebDavStepState.Bad => Color.FromArgb(32, p.Danger.R, p.Danger.G, p.Danger.B),
                WebDavStepState.Running => tint.Soft,
                _ => p.Inset,
            };
            Color badgeFg = s.State switch
            {
                WebDavStepState.Ok => p.Ok,
                WebDavStepState.Warn => Color.FromArgb(255, 196, 128, 24),
                WebDavStepState.Bad => p.Danger,
                WebDavStepState.Running => tint.Accent,
                _ => p.T3,
            };
            string badgeSym = s.State switch
            {
                WebDavStepState.Ok => "✓",
                WebDavStepState.Warn => "!",
                WebDavStepState.Bad => "✕",
                _ => "·",
            };

            UIElement badgeChild = s.State == WebDavStepState.Running
                ? new ProgressRing
                {
                    Width = 12,
                    Height = 12,
                    IsActive = true,
                    Foreground = ThemeHelper.Brush(tint.Accent),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                }
                : new TextBlock
                {
                    Text = badgeSym,
                    FontSize = 10.5,
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    Foreground = ThemeHelper.Brush(badgeFg),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };

            var badge = new Border
            {
                Width = 19,
                Height = 19,
                CornerRadius = new CornerRadius(9.5),
                Background = ThemeHelper.Brush(badgeBg),
                BorderBrush = s.State == WebDavStepState.Untested ? ThemeHelper.Brush(p.Line) : new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(s.State == WebDavStepState.Untested ? 1 : 0),
                Child = badgeChild,
            };
            leftCol.Children.Add(badge);

            if (!isLast)
            {
                var line = new Border
                {
                    Width = 1,
                    MinHeight = 12,
                    Background = ThemeHelper.Brush(p.Line),
                    HorizontalAlignment = HorizontalAlignment.Center,
                };
                Grid.SetRow(line, 1);
                leftCol.Children.Add(line);
            }
            rowGrid.Children.Add(leftCol);

            var rightStack = new StackPanel { Spacing = 4, Padding = new Thickness(0, 1, 0, isLast ? 2 : 10) };
            var titleLine = new Grid();
            titleLine.Children.Add(new TextBlock
            {
                Text = s.Title,
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.Medium,
                Foreground = ThemeHelper.Brush(s.State == WebDavStepState.Untested ? p.T3 : p.T1),
            });
            titleLine.Children.Add(new TextBlock
            {
                Text = s.Detail,
                FontSize = 11,
                Foreground = ThemeHelper.Brush(s.State == WebDavStepState.Bad ? p.Danger : (s.State == WebDavStepState.Running ? tint.Accent : p.T3)),
                HorizontalAlignment = HorizontalAlignment.Right,
            });
            rightStack.Children.Add(titleLine);

            if (!string.IsNullOrEmpty(s.FixHint))
            {
                var fixStack = new StackPanel { Spacing = 6 };
                fixStack.Children.Add(new TextBlock
                {
                    Text = s.FixHint,
                    FontSize = 11,
                    Foreground = ThemeHelper.Brush(p.T2),
                    TextWrapping = TextWrapping.Wrap,
                });
                if (!string.IsNullOrEmpty(s.FixLinkLabel) && !string.IsNullOrEmpty(s.FixLinkUrl))
                {
                    var linkBtn = new Button
                    {
                        Style = (Style)Application.Current.Resources["YYFluentButtonStyle"],
                        Height = 24,
                        MinHeight = 24,
                        FontSize = 11,
                        Content = s.FixLinkLabel,
                    };
                    string targetUrl = s.FixLinkUrl!;
                    linkBtn.Click += (_, _) => _ = Launcher.LaunchUriAsync(new Uri(targetUrl));
                    fixStack.Children.Add(linkBtn);
                }
                rightStack.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(8),
                    Background = ThemeHelper.Brush(p.Inset),
                    Padding = new Thickness(10, 8, 10, 8),
                    Margin = new Thickness(0, 2, 0, 0),
                    Child = fixStack,
                });
            }

            Grid.SetColumn(rightStack, 1);
            rowGrid.Children.Add(rightStack);
            timeline.Children.Add(rowGrid);
        }

        outer.Children.Add(timeline);
        return new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 10, 12, 10),
            Child = outer,
        };
    }
}
