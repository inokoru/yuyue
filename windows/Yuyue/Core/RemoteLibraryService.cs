using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Yuyue.Audiobook;
using Yuyue.Design;

namespace Yuyue.Core;

public sealed record RemoteSourceConfig(
    string Id,
    string Name,
    string Kind, // "opds" | "webdav"
    string Url,
    string Username);

public sealed record RemoteEntryItem(
    string Title,
    string Subtitle,
    string Url,
    bool IsFolder,
    string? FormatExt,
    long? SizeBytes);

/// <summary>
/// 1:1 port of <c>apple/Yuyue/App/RemoteLibrary.swift</c>:
/// OPDS 1.x Atom catalogue browser (with <c>{searchTerms}</c> search, pagination &amp; format ranking)
/// and WebDAV remote book/audiobook folder browser + on-demand Shelf cloud book downloader.
/// </summary>
public sealed class RemoteLibraryService
{
    private static readonly string[] FormatPriority =
        ["epub", "azw3", "mobi", "azw", "pdf", "txt", "m4b", "m4a", "mp3", "aac", "flac", "ogg", "opus", "wav"];

    private readonly LibraryService _library;

    public RemoteLibraryService(LibraryService library)
    {
        _library = library;
    }

    public List<RemoteSourceConfig> GetSources()
    {
        if (string.IsNullOrWhiteSpace(_library.Prefs.RemoteSourcesJson)) return [];
        try { return JsonSerializer.Deserialize<List<RemoteSourceConfig>>(_library.Prefs.RemoteSourcesJson) ?? []; }
        catch { return []; }
    }

    public void SaveSources(List<RemoteSourceConfig> sources)
    {
        _library.Prefs.RemoteSourcesJson = JsonSerializer.Serialize(sources);
        _library.SavePrefs();
    }

    public void RemoveSource(string id)
    {
        var list = GetSources();
        list.RemoveAll(s => s.Id == id);
        _library.Secrets.Set($"source.{id}.password", "");
        SaveSources(list);
    }

    private HttpClient CreateClient(RemoteSourceConfig src)
    {
        string pwd = _library.Secrets.Get($"source.{src.Id}.password");
        var handler = new HttpClientHandler();
        if (!string.IsNullOrEmpty(src.Username) || !string.IsNullOrEmpty(pwd))
        {
            handler.Credentials = new NetworkCredential(src.Username, pwd);
            handler.PreAuthenticate = true;
        }
        var client = new HttpClient(handler) { Timeout = System.TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("语阅/0.1");
        if (!string.IsNullOrEmpty(src.Username))
        {
            string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{src.Username}:{pwd}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", b64);
        }
        return client;
    }

    public async Task<(IReadOnlyList<RemoteEntryItem> Items, string? SearchTemplate, string? NextPageUrl)> FetchPageAsync(
        RemoteSourceConfig src, string? overrideUrl = null, CancellationToken ct = default)
    {
        string targetUrl = overrideUrl ?? src.Url;
        using var client = CreateClient(src);

        if (src.Kind == "webdav")
        {
            if (!targetUrl.EndsWith('/')) targetUrl += "/";
            var baseUri = new Uri(targetUrl);
            using var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), baseUri);
            req.Headers.Add("Depth", "1");
            using var resp = await client.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();

            string xml = await resp.Content.ReadAsStringAsync(ct);
            var doc = XDocument.Parse(xml);
            XNamespace dav = "DAV:";

            var folders = new List<RemoteEntryItem>();
            var files = new List<RemoteEntryItem>();

            foreach (var r in doc.Descendants(dav + "response"))
            {
                string? href = r.Element(dav + "href")?.Value;
                if (string.IsNullOrEmpty(href)) continue;
                var itemUri = new Uri(baseUri, href);
                if (Uri.Compare(itemUri, baseUri, UriComponents.Path, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0)
                    continue;

                bool isDir = r.Descendants(dav + "collection").Any();
                string rawName = Uri.UnescapeDataString(itemUri.Segments.LastOrDefault()?.Trim('/') ?? "");
                if (string.IsNullOrEmpty(rawName) || rawName.StartsWith('.')) continue;

                if (isDir)
                {
                    folders.Add(new RemoteEntryItem(rawName, "文件夹", itemUri.ToString(), true, null, null));
                }
                else
                {
                    string ext = Path.GetExtension(rawName).TrimStart('.').ToLowerInvariant();
                    if (!FormatPriority.Contains(ext)) continue;
                    long? len = long.TryParse(r.Descendants(dav + "getcontentlength").FirstOrDefault()?.Value, out var l) ? l : null;
                    string sizeNote = len is > 0 ? $"{len.Value / 1024.0 / 1024.0:0.1} MB" : "";
                    files.Add(new RemoteEntryItem(
                        Path.GetFileNameWithoutExtension(rawName),
                        string.IsNullOrEmpty(sizeNote) ? ext.ToUpperInvariant() : $"{ext.ToUpperInvariant()} · {sizeNote}",
                        itemUri.ToString(),
                        false,
                        ext,
                        len));
                }
            }

            var combined = folders.OrderBy(x => x.Title).Concat(files.OrderBy(x => x.Title)).ToArray();
            return (combined, null, null);
        }
        else
        {
            var baseUri = new Uri(targetUrl);
            string xml = await client.GetStringAsync(baseUri, ct);
            var doc = XDocument.Parse(xml);
            XNamespace atom = "http://www.w3.org/2005/Atom";

            string? nextUrl = null;
            string? searchTpl = null;
            foreach (var link in doc.Root?.Elements(atom + "link") ?? Enumerable.Empty<XElement>())
            {
                string rel = link.Attribute("rel")?.Value ?? "";
                string type = link.Attribute("type")?.Value ?? "";
                string? href = link.Attribute("href")?.Value;
                if (string.IsNullOrEmpty(href)) continue;
                string resolved = UpgradeIfHttps(baseUri, new Uri(baseUri, href)).ToString();

                if (rel == "next") nextUrl = resolved;
                else if (rel == "search" && resolved.Contains("{searchTerms}", StringComparison.Ordinal))
                    searchTpl = resolved;
            }

            var items = new List<RemoteEntryItem>();
            foreach (var entry in doc.Descendants(atom + "entry"))
            {
                string title = entry.Element(atom + "title")?.Value.Trim() ?? "未命名条目";
                string author = entry.Element(atom + "author")?.Element(atom + "name")?.Value.Trim() ?? "";
                var links = entry.Elements(atom + "link").ToArray();

                // Check acquisition links ranked by FormatPriority
                (string Url, string Ext)? bestAcq = null;
                int bestRank = int.MaxValue;
                string? subFeedUrl = null;

                foreach (var lk in links)
                {
                    string rel = lk.Attribute("rel")?.Value ?? "";
                    string type = (lk.Attribute("type")?.Value ?? "").ToLowerInvariant();
                    string? href = lk.Attribute("href")?.Value;
                    if (string.IsNullOrEmpty(href)) continue;
                    string full = UpgradeIfHttps(baseUri, new Uri(baseUri, href)).ToString();

                    if (type.Contains("application/atom+xml") && !rel.Contains("acquisition"))
                    {
                        subFeedUrl ??= full;
                        continue;
                    }

                    string? ext = InferFormatFromLink(type, full);
                    if (ext is not null && (rel.Contains("acquisition") || FormatPriority.Contains(ext)))
                    {
                        int rank = Array.IndexOf(FormatPriority, ext);
                        if (rank >= 0 && rank < bestRank)
                        {
                            bestRank = rank;
                            bestAcq = (full, ext);
                        }
                    }
                }

                if (bestAcq is var (acqUrl, acqExt))
                {
                    string sub = string.IsNullOrEmpty(author) ? acqExt.ToUpperInvariant() : $"{author} · {acqExt.ToUpperInvariant()}";
                    items.Add(new RemoteEntryItem(title, sub, acqUrl, false, acqExt, null));
                }
                else if (subFeedUrl is not null)
                {
                    string sum = entry.Element(atom + "content")?.Value.Trim() ?? entry.Element(atom + "summary")?.Value.Trim() ?? "分类目录";
                    items.Add(new RemoteEntryItem(title, sum, subFeedUrl, true, null, null));
                }
            }

            return (items, searchTpl, nextUrl);
        }
    }

    public async Task<BookSummary> DownloadAndImportEntryAsync(RemoteSourceConfig src, RemoteEntryItem item, CancellationToken ct = default)
    {
        using var client = CreateClient(src);
        string ext = "." + (item.FormatExt ?? "epub").TrimStart('.');
        string tempDir = Path.Combine(Path.GetTempPath(), "yuyue-remote-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        string safeName = string.Concat(item.Title.Split(Path.GetInvalidFileNameChars())).Trim();
        if (string.IsNullOrEmpty(safeName)) safeName = "book";
        string dest = Path.Combine(tempDir, safeName + ext);

        try
        {
            byte[] bytes = await client.GetByteArrayAsync(item.Url, ct);
            await File.WriteAllBytesAsync(dest, bytes, ct);
            if (AudiobookImporter.Extensions.Contains(ext.ToLowerInvariant()))
                return await AudiobookImporter.ImportFilesAsync(_library, [dest]);
            return await Task.Run(() => _library.ImportBook(dest), ct);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    public async Task ShowRemoteLibraryModalAsync(XamlRoot xamlRoot, Action onImported)
    {
        bool dark = ThemeHelper.IsDark(xamlRoot.Content is FrameworkElement fe ? fe.ActualTheme : ElementTheme.Light);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);

        var sources = GetSources();
        RemoteSourceConfig? activeSrc = sources.FirstOrDefault();
        var navStack = new Stack<string>();
        string? currentSearchTpl = null;

        var root = new Grid { Width = 580, Height = 440, ColumnSpacing = 12 };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(168) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var leftList = new StackPanel { Spacing = 4 };
        var rightStack = new Grid { RowSpacing = 8 };
        rightStack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rightStack.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        rightStack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var topBar = new Grid { ColumnSpacing = 6 };
        topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var backBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "‹ 上级", Visibility = Visibility.Collapsed };
        var searchBox = new TextBox { PlaceholderText = "搜索 OPDS 书目（回车）…", Visibility = Visibility.Collapsed };
        topBar.Children.Add(backBtn);
        Grid.SetColumn(searchBox, 1);
        topBar.Children.Add(searchBox);
        rightStack.Children.Add(topBar);

        var entriesPanel = new StackPanel { Spacing = 4 };
        var scroll = new ScrollViewer { Content = entriesPanel };
        Grid.SetRow(scroll, 1);
        rightStack.Children.Add(scroll);

        var statusText = new TextBlock { FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T3) };
        Grid.SetRow(statusText, 2);
        rightStack.Children.Add(statusText);

        async Task LoadUrlAsync(string? url = null)
        {
            entriesPanel.Children.Clear();
            backBtn.Visibility = navStack.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (activeSrc is null)
            {
                statusText.Text = "先在左侧添加一个 Calibre-Web (OPDS) 或 WebDAV 书库源。";
                return;
            }
            statusText.Text = $"正在读取 {activeSrc.Name}…";
            try
            {
                var (items, searchTpl, nextUrl) = await FetchPageAsync(activeSrc, url);
                if (searchTpl is not null) currentSearchTpl = searchTpl;
                searchBox.Visibility = currentSearchTpl is not null ? Visibility.Visible : Visibility.Collapsed;
                statusText.Text = $"共 {items.Count} 项";

                foreach (var it in items)
                {
                    var entry = it;
                    var row = new Grid { ColumnSpacing = 10, Padding = new Thickness(10, 7, 10, 7) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    row.Children.Add(YYIcons.Make(entry.IsFolder ? "folder" : "book", 13.5, ThemeHelper.Brush(entry.IsFolder ? tint.Accent : p.T2)));
                    var meta = new StackPanel { Spacing = 1 };
                    meta.Children.Add(new TextBlock { Text = entry.Title, FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
                    meta.Children.Add(new TextBlock { Text = entry.Subtitle, FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
                    Grid.SetColumn(meta, 1);
                    row.Children.Add(meta);

                    var actBtn = new Button
                    {
                        Style = (Style)Application.Current.Resources[entry.IsFolder ? "YYPillButtonStyle" : "YYPrimaryPillButtonStyle"],
                        Content = entry.IsFolder ? "打开 ›" : "放入书架",
                        Height = 25,
                        MinHeight = 25,
                        FontSize = 11,
                    };
                    actBtn.Click += async (_, _) =>
                    {
                        if (entry.IsFolder)
                        {
                            navStack.Push(url ?? activeSrc.Url);
                            await LoadUrlAsync(entry.Url);
                        }
                        else
                        {
                            actBtn.IsEnabled = false;
                            actBtn.Content = "下载中…";
                            try
                            {
                                var b = await DownloadAndImportEntryAsync(activeSrc, entry);
                                actBtn.Content = "✓ 已在书架";
                                statusText.Text = $"已导入《{b.Title}》";
                                onImported();
                            }
                            catch (Exception ex)
                            {
                                actBtn.IsEnabled = true;
                                actBtn.Content = "重试";
                                statusText.Text = $"下载失败：{ex.Message}";
                            }
                        }
                    };
                    Grid.SetColumn(actBtn, 2);
                    row.Children.Add(actBtn);

                    entriesPanel.Children.Add(new Border
                    {
                        CornerRadius = new CornerRadius(8),
                        Background = ThemeHelper.Brush(p.Card),
                        BorderBrush = ThemeHelper.Brush(p.Line),
                        BorderThickness = new Thickness(1),
                        Child = row,
                    });
                }

                if (!string.IsNullOrEmpty(nextUrl))
                {
                    var moreBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], HorizontalAlignment = HorizontalAlignment.Center, Content = "下一页 ›" };
                    string nu = nextUrl!;
                    moreBtn.Click += async (_, _) => { navStack.Push(url ?? activeSrc.Url); await LoadUrlAsync(nu); };
                    entriesPanel.Children.Add(moreBtn);
                }
            }
            catch (Exception ex)
            {
                statusText.Text = $"读取失败：{ex.Message}";
            }
        }

        void RebuildSourcesSidebar()
        {
            leftList.Children.Clear();
            leftList.Children.Add(new TextBlock { Text = "远程书库", FontSize = 11.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T3), Margin = new Thickness(4, 0, 0, 4) });
            foreach (var s in sources)
            {
                var src = s;
                bool sel = activeSrc?.Id == src.Id;
                var b = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    CornerRadius = new CornerRadius(8),
                    Background = sel ? ThemeHelper.Brush(tint.Soft) : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(10, 7, 10, 7),
                    Content = new StackPanel
                    {
                        Spacing = 1,
                        Children =
                        {
                            new TextBlock { Text = src.Name, FontSize = 12.5, FontWeight = sel ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal, Foreground = ThemeHelper.Brush(sel ? tint.Accent : p.T1) },
                            new TextBlock { Text = src.Kind.ToUpperInvariant(), FontSize = 10, Foreground = ThemeHelper.Brush(p.T3) },
                        },
                    },
                };
                var ctx = new MenuFlyout();
                var delMi = new MenuFlyoutItem { Text = "移除此书库源" };
                delMi.Click += (_, _) =>
                {
                    RemoveSource(src.Id);
                    sources = GetSources();
                    activeSrc = sources.FirstOrDefault();
                    RebuildSourcesSidebar();
                    _ = LoadUrlAsync();
                };
                ctx.Items.Add(delMi);
                b.ContextFlyout = ctx;
                b.Click += async (_, _) =>
                {
                    activeSrc = src;
                    navStack.Clear();
                    currentSearchTpl = null;
                    RebuildSourcesSidebar();
                    await LoadUrlAsync();
                };
                leftList.Children.Add(b);
            }

            // Inline quick-add card
            var nameIn = new TextBox { PlaceholderText = "名称（如 Calibre 书库）", FontSize = 11.5 };
            var urlIn = new TextBox { PlaceholderText = "OPDS 或 WebDAV 地址", FontSize = 11.5 };
            var userIn = new TextBox { PlaceholderText = "账号（可选）", FontSize = 11.5 };
            var pwdIn = new PasswordBox { PlaceholderText = "密码（可选）", FontSize = 11.5 };
            var kindCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, SelectedIndex = 0, FontSize = 11.5 };
            kindCombo.Items.Add(new ComboBoxItem { Content = "OPDS 目录 (Calibre / Komga)", Tag = "opds" });
            kindCombo.Items.Add(new ComboBoxItem { Content = "WebDAV 文件夹", Tag = "webdav" });

            var addSrcBtn = new Button { Style = (Style)Application.Current.Resources["YYPrimaryPillButtonStyle"], HorizontalAlignment = HorizontalAlignment.Stretch, Content = "＋ 添加书库源", Height = 26, FontSize = 11.5 };
            addSrcBtn.Click += async (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(urlIn.Text)) return;
                string id = Guid.NewGuid().ToString("N")[..10];
                string kind = (kindCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "opds";
                string nm = string.IsNullOrWhiteSpace(nameIn.Text) ? new Uri(urlIn.Text.Trim()).Host : nameIn.Text.Trim();
                var added = new RemoteSourceConfig(id, nm, kind, urlIn.Text.Trim(), userIn.Text.Trim());
                if (!string.IsNullOrEmpty(pwdIn.Password)) _library.Secrets.Set($"source.{id}.password", pwdIn.Password);
                sources.Add(added);
                SaveSources(sources);
                activeSrc = added;
                navStack.Clear();
                RebuildSourcesSidebar();
                await LoadUrlAsync();
            };

            leftList.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(9),
                Background = ThemeHelper.Brush(p.Inset),
                Padding = new Thickness(8),
                Margin = new Thickness(0, 8, 0, 0),
                Child = new StackPanel { Spacing = 5, Children = { kindCombo, nameIn, urlIn, userIn, pwdIn, addSrcBtn } },
            });
        }

        backBtn.Click += async (_, _) =>
        {
            if (navStack.Count > 0) await LoadUrlAsync(navStack.Pop());
        };
        searchBox.KeyDown += async (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter && currentSearchTpl is not null && !string.IsNullOrWhiteSpace(searchBox.Text))
            {
                navStack.Push(activeSrc?.Url ?? "");
                await LoadUrlAsync(currentSearchTpl.Replace("{searchTerms}", Uri.EscapeDataString(searchBox.Text.Trim())));
            }
        };

        RebuildSourcesSidebar();
        _ = LoadUrlAsync();

        root.Children.Add(new ScrollViewer { Content = leftList });
        Grid.SetColumn(rightStack, 1);
        root.Children.Add(rightStack);

        await new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "OPDS 与 WebDAV 远程书库",
            Content = root,
            CloseButtonText = "完成",
        }.ShowAsync();
    }

    private static Uri UpgradeIfHttps(Uri baseUri, Uri target)
    {
        if (baseUri.Scheme == "https" && target.Scheme == "http" && string.Equals(baseUri.Host, target.Host, StringComparison.OrdinalIgnoreCase))
        {
            var ub = new UriBuilder(target) { Scheme = "https", Port = -1 };
            return ub.Uri;
        }
        return target;
    }

    private static string? InferFormatFromLink(string mime, string url)
    {
        if (mime.Contains("epub")) return "epub";
        if (mime.Contains("pdf")) return "pdf";
        if (mime.Contains("mobipocket") || mime.Contains("x-mobi")) return "mobi";
        if (mime.Contains("audiobook") || mime.Contains("mp4")) return "m4b";
        if (mime.Contains("mpeg")) return "mp3";

        try
        {
            string ext = Path.GetExtension(new Uri(url).AbsolutePath).TrimStart('.').ToLowerInvariant();
            if (FormatPriority.Contains(ext)) return ext;
        }
        catch { }
        return null;
    }
}
