using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.Storage.Streams;
using Yuyue.Core;

namespace Yuyue.Reader;

/// <summary>
/// Hosts the shared TypeScript reader bundle inside WinUI 3's <see cref="WebView2"/>.
/// Serves <c>https://yuyue.local/app/*</c> from bundled reader assets and
/// <c>https://yuyue.local/book/*</c> directly from <see cref="YuyueCore.Serve"/> (zero temp files).
/// Shares one prewarmed <see cref="CoreWebView2Environment"/> across all reader &amp; dictionary WebViews.
/// </summary>
public sealed class ReaderWebViewHost : IDisposable
{
    public const string Origin = "https://yuyue.local";
    public const string WebView2DownloadUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

    private static readonly object EnvLock = new();
    private static Task<CoreWebView2Environment>? _sharedEnvTask;
    private static string? _sharedEnvDataDir;

    public static bool IsWebView2RuntimeAvailable(out string? version)
    {
        try
        {
            string v = CoreWebView2Environment.GetAvailableBrowserVersionString();
            version = string.IsNullOrWhiteSpace(v) ? null : v;
            return version is not null;
        }
        catch
        {
            version = null;
            return false;
        }
    }

    public static Task<CoreWebView2Environment> GetSharedEnvironmentAsync(string dataDir)
    {
        string userDataDir = Path.Combine(dataDir, "webview2");
        lock (EnvLock)
        {
            if (_sharedEnvTask is not null &&
                string.Equals(_sharedEnvDataDir, userDataDir, StringComparison.OrdinalIgnoreCase) &&
                !_sharedEnvTask.IsFaulted &&
                !_sharedEnvTask.IsCanceled)
            {
                return _sharedEnvTask;
            }
            Directory.CreateDirectory(userDataDir);
            _sharedEnvDataDir = userDataDir;
            _sharedEnvTask = CoreWebView2Environment.CreateWithOptionsAsync(null, userDataDir, null).AsTask();
            return _sharedEnvTask;
        }
    }

    public static void PrewarmAsync(string dataDir)
    {
        _ = Task.Run(async () =>
        {
            try { await GetSharedEnvironmentAsync(dataDir); }
            catch { }
        });
    }

    private readonly WebView2 _webView;
    private readonly LibraryService _library;
    private readonly Func<PageGeometry> _geometryProvider;
    private readonly string _readerAssetsDir;

    private bool _initialized;
    private long _generation;

    public WebView2 WebView => _webView;
    public bool IsInitialized => _initialized;
    public long Generation => _generation;

    public event Action<ReaderMessage>? MessageReceived;

    public ReaderWebViewHost(WebView2 webView, LibraryService library, Func<PageGeometry> geometryProvider)
    {
        _webView = webView;
        _library = library;
        _geometryProvider = geometryProvider;
        _readerAssetsDir = Path.Combine(AppContext.BaseDirectory, "Assets", "Reader");
    }

    public async Task InitializeAsync()
    {
        if (_initialized) return;

        var env = await GetSharedEnvironmentAsync(_library.DataDir);
        await _webView.EnsureCoreWebView2Async(env);

        var core = _webView.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsBuiltInErrorPageEnabled = false;
#if DEBUG
        core.Settings.AreDevToolsEnabled = true;
#else
        core.Settings.AreDevToolsEnabled = false;
#endif

        core.AddWebResourceRequestedFilter($"{Origin}/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnWebResourceRequested;
        core.WebMessageReceived += OnWebMessageReceived;
        core.NewWindowRequested += OnNewWindowRequested;

        // Windows desktop ergonomics + reliable keyboard interception when WebView2 holds focus:
        // 1. Mouse wheel turns pages in paginated mode; Ctrl+Wheel zooms font size.
        // 2. Right-click forwards coordinates so WinUI 3 shows a native context MenuFlyout.
        // 3. Reader navigation/shortcut keys are forwarded to WinUI before Chromium swallows them.
        await core.AddScriptToExecuteOnDocumentCreatedAsync(@"
            (() => {
                let lastWheel = 0;
                let lastWheelAt = -1000;
                let lastScrollMove = -1000;
                let wheelQuietSinceScroll = true;
                let lastEdgeDir = 0;
                let lastEdgeTime = 0;
                let edgeAccum = 0;
                let lastCross = 0;
                let edgeReleaseTimer = 0;
                function clearEdgePull() {
                    edgeAccum = 0;
                    lastEdgeDir = 0;
                    if (edgeReleaseTimer) { clearTimeout(edgeReleaseTimer); edgeReleaseTimer = 0; }
                }
                window.addEventListener('scroll', () => {
                    lastScrollMove = performance.now();
                    wheelQuietSinceScroll = false;
                    clearEdgePull();
                }, { passive: true });
                window.addEventListener('wheel', (e) => {
                    if (e.ctrlKey) {
                        e.preventDefault();
                        window.chrome?.webview?.postMessage({ type: 'wheel', deltaY: e.deltaY, ctrl: true });
                        return;
                    }
                    if (Math.abs(e.deltaY) < Math.abs(e.deltaX) || e.deltaY === 0) return;
                    if (document.documentElement.dataset.yyMode !== 'scrolled') {
                        e.preventDefault();
                        const now = performance.now();
                        if (now - lastWheel < 200) return;
                        lastWheel = now;
                        window.chrome?.webview?.postMessage({ type: 'wheel', deltaY: e.deltaY, ctrl: false });
                        return;
                    }
                    const now = performance.now();
                    const wheelGap = now - lastWheelAt;
                    lastWheelAt = now;
                    if (wheelGap > 180) wheelQuietSinceScroll = true;

                    const el = document.scrollingElement || document.documentElement;
                    const maxY = Math.max(0, el.scrollHeight - window.innerHeight);
                    const y = window.pageYOffset;
                    const forward = e.deltaY > 0;
                    const atEdge = forward ? (maxY <= 1 || y >= maxY - 2) : (y <= 2);
                    if (!atEdge) {
                        wheelQuietSinceScroll = false;
                        clearEdgePull();
                        return;
                    }
                    // Require an explicit >180ms wheel quiet gap after scrolling hits 0/maxY so touchpad inertia never false-triggers chapter cross.
                    if (edgeAccum === 0 && (!wheelQuietSinceScroll || now - lastScrollMove < 180)) return;
                    const dir = forward ? 1 : -1;
                    if (dir !== lastEdgeDir || now - lastEdgeTime > 900) {
                        clearEdgePull();
                        lastEdgeDir = dir;
                    }
                    lastEdgeTime = now;
                    const rawMag = e.deltaMode === 1 ? Math.abs(e.deltaY) * 40 : (e.deltaMode === 2 ? 200 : Math.abs(e.deltaY));
                    edgeAccum += Math.min(rawMag, 110);
                    if (edgeReleaseTimer) { clearTimeout(edgeReleaseTimer); edgeReleaseTimer = 0; }
                    if (edgeAccum >= 220 && now - lastCross > 500) {
                        clearEdgePull();
                        lastCross = now;
                        wheelQuietSinceScroll = false;
                        window.chrome?.webview?.postMessage({ type: 'scrollEdge', deltaY: dir, armed: true, progress: 1 });
                    } else if (now - lastCross > 500) {
                        const prog = Math.min(1, edgeAccum / 220);
                        window.chrome?.webview?.postMessage({ type: 'scrollEdge', deltaY: dir, armed: false, progress: prog });
                        if (prog >= 0.65) {
                            edgeReleaseTimer = setTimeout(() => {
                                edgeReleaseTimer = 0;
                                const curNow = performance.now();
                                if (edgeAccum >= 143 && lastEdgeDir === dir && curNow - lastCross > 500) {
                                    clearEdgePull();
                                    lastCross = curNow;
                                    wheelQuietSinceScroll = false;
                                    window.chrome?.webview?.postMessage({ type: 'scrollEdge', deltaY: dir, armed: true, progress: 1 });
                                }
                            }, 160);
                        }
                    }
                }, { passive: false });
                window.addEventListener('contextmenu', (e) => {
                    if (e.defaultPrevented) return;
                    e.preventDefault();
                    if (!window.yy) {
                        window.chrome?.webview?.postMessage({ type: 'winContext', x: e.clientX, y: e.clientY });
                    }
                });
                window.addEventListener('keydown', (e) => {
                    const tag = (document.activeElement && document.activeElement.tagName) || '';
                    if (tag === 'INPUT' || tag === 'TEXTAREA') return;
                    const ctrl = e.ctrlKey || e.metaKey;
                    const alt = e.altKey;
                    const shift = e.shiftKey;
                    const k = e.key;
                    const c = e.code || '';
                    if (ctrl && (k === 'c' || k === 'C' || k === 'a' || k === 'A')) return;
                    const isCtrlShortcut = ctrl && (
                        ['f','F','k','K','g','G','d','D','b','B','i','I','z','Z','w','W','0','1','2','3','4','5','6','7','8','9','[',']','=','+','-','_',',','Tab'].includes(k) ||
                        (shift && (k === 't' || k === 'T')) ||
                        c === 'BracketLeft' || c === 'BracketRight' || /^Digit[0-9]$/.test(c) || /^Numpad[0-9]$/.test(c) ||
                        (alt && (['t','T','n','N','ArrowUp','ArrowDown'].includes(k) || c === 'KeyT' || c === 'KeyN'))
                    );
                    const isAltShortcut = !ctrl && alt && (k === 'ArrowLeft' || k === 'ArrowRight');
                    const isNavKey = !ctrl && !alt && (
                        ['ArrowLeft','ArrowRight','ArrowUp','ArrowDown','PageUp','PageDown',' ','Escape','F3','F11','1','2','3','4','c','C','n','N','Delete','Backspace'].includes(k) ||
                        c === 'Numpad1' || c === 'Numpad2' || c === 'Numpad3' || c === 'Numpad4'
                    );
                    if (isCtrlShortcut || isAltShortcut || isNavKey) {
                        e.preventDefault();
                        e.stopPropagation();
                        window.chrome?.webview?.postMessage({ type: 'winKey', key: k, code: c, ctrl, shift, alt });
                    }
                }, true);
            })();
        ");

        _initialized = true;
    }

    public void LoadSection(string bookId, uint section)
    {
        if (!_initialized) return;
        _generation++;
        _webView.CoreWebView2.Navigate($"{Origin}/book/{bookId}/s/{section}.html?g={_generation}");
    }

    public void ClearBlank()
    {
        if (!_initialized) return;
        _generation++;
        _webView.CoreWebView2.Navigate("about:blank");
    }

    public Task<string> CallAsync(string script)
    {
        if (!_initialized || _webView.CoreWebView2 is null) return Task.FromResult("null");
        return _webView.CoreWebView2.ExecuteScriptAsync(script).AsTask();
    }

    public Task ApplySettingsAsync(object settingsPayload)
    {
        var json = JsonSerializer.Serialize(settingsPayload, ReaderMessage.JsonOptions);
        return CallAsync($"window.yy && window.yy.applySettings({json});");
    }

    public async Task<bool> NextPageAsync(bool animated = true)
    {
        var result = await CallAsync($"window.yy ? window.yy.nextPage({(animated ? "true" : "false")}) : false");
        return result.Trim() == "true";
    }

    public async Task<bool> PrevPageAsync(bool animated = true)
    {
        var result = await CallAsync($"window.yy ? window.yy.prevPage({(animated ? "true" : "false")}) : false");
        return result.Trim() == "true";
    }

    public Task ScrollToPageAsync(int page, bool animated = false) =>
        CallAsync($"window.yy && window.yy.scrollToPage({page}, {(animated ? "true" : "false")});");

    public Task ScrollToStartOfSectionAsync() =>
        CallAsync(@"(() => {
            if (document.documentElement.dataset.yyMode === 'scrolled') {
                window.scrollTo(0, 0);
            } else if (window.yy) {
                window.yy.scrollToPage(0, false);
            }
        })();");

    public Task ScrollToEndOfSectionAsync(int? pageCount = null)
    {
        int lastPage = Math.Max(0, (pageCount ?? 1) - 1);
        return CallAsync($@"(() => {{
            if (document.documentElement.dataset.yyMode === 'scrolled') {{
                const jumpBottom = () => {{
                    const el = document.scrollingElement || document.documentElement;
                    window.scrollTo(0, Math.max(0, el.scrollHeight));
                }};
                jumpBottom();
                requestAnimationFrame(jumpBottom);
            }} else if (window.yy) {{
                window.yy.scrollToPage({lastPage}, false);
            }}
        }})();");
    }

    public Task ClearTransientOverlaysAsync() =>
        CallAsync(@"(() => {
            if (!window.yy) return;
            window.yy.setActive(null);
            window.yy.setHits(null);
            window.yy.clearSelection();
            window.yy.setSource(null);
            window.yy.setFlag(null);
        })();");

    public Task ScrollToSentenceAsync(int sentence, bool animated = false, string? align = null)
    {
        string opts = align is "top" or "third" ? $", {{ align: '{align}' }}" : "";
        return CallAsync($"window.yy && window.yy.scrollToSentence({sentence}, {(animated ? "true" : "false")}{opts});");
    }

    public Task ScrollToAnchorAsync(string anchor, bool animated = false, string? align = null)
    {
        var escaped = JsonSerializer.Serialize(anchor);
        string opts = align is "top" or "third" ? $", {{ align: '{align}' }}" : "";
        return CallAsync($"window.yy && window.yy.scrollToAnchor({escaped}, {(animated ? "true" : "false")}{opts});");
    }

    public async Task<int> CurrentPageAsync()
    {
        var raw = await CallAsync("window.yy ? window.yy.currentPage() : 0");
        return int.TryParse(raw.Trim(), out int p) ? p : 0;
    }

    public Task SetActiveAsync(int? sentence, int durationMs = 0, int elapsedMs = 0, bool playing = true, int[]? words = null)
    {
        if (sentence is null)
            return CallAsync("window.yy && window.yy.setActive(null);");
        string wordsPart = words is { Length: >= 4 } ? $", words: {JsonSerializer.Serialize(words)}" : "";
        return CallAsync($"window.yy && window.yy.setActive({sentence.Value}, {{ durationMs: {durationMs}, elapsedMs: {elapsedMs}, playing: {(playing ? "true" : "false")}{wordsPart} }});");
    }

    public Task SetPlayingAsync(bool playing) =>
        CallAsync($"window.yy && window.yy.setPlaying({(playing ? "true" : "false")});");

    public Task SetUiAsync(string ui)
    {
        var escaped = JsonSerializer.Serialize(ui == "en" ? "en" : "zh");
        return CallAsync($"window.yy && window.yy.setUi({escaped});");
    }

    public Task SetSourceAsync(int? sentence)
    {
        if (sentence is null) return CallAsync("window.yy && window.yy.setSource(null);");
        return CallAsync($"window.yy && window.yy.setSource({sentence.Value});");
    }

    public Task SetFlagAsync(int? first, int? last = null)
    {
        if (first is null) return CallAsync("window.yy && window.yy.setFlag(null);");
        return last is int l
            ? CallAsync($"window.yy && window.yy.setFlag({first.Value}, {l});")
            : CallAsync($"window.yy && window.yy.setFlag({first.Value});");
    }

    public Task SetUnreadAsync(IEnumerable<int>? sentences, string? tag = null)
    {
        if (sentences is null) return CallAsync("window.yy && window.yy.setUnread(null);");
        var list = sentences.ToArray();
        if (list.Length == 0) return CallAsync("window.yy && window.yy.setUnread(null);");
        var json = JsonSerializer.Serialize(list);
        var tagJs = tag is null ? "null" : JsonSerializer.Serialize(tag);
        return CallAsync($"window.yy && window.yy.setUnread({json}, {tagJs});");
    }

    public Task SetBubblesAsync(IEnumerable<BubbleLine>? lines)
    {
        if (lines is null)
            return CallAsync("window.yy && window.yy.setBubbles(null);");

        var payload = lines.Select(b => new
        {
            first = b.First,
            last = b.Last,
            start = b.Start,
            end = b.End,
            who = b.Who,
            avatar = b.Avatar,
            color = b.Color,
            right = b.Right,
            choices = b.Choices,
        });
        var json = JsonSerializer.Serialize(payload);
        return CallAsync($"window.yy && window.yy.setBubbles({json});");
    }

    public Task SetMarksAsync(IEnumerable<Mark> marks)
    {
        var list = marks.Select(m => new
        {
            id = m.Id,
            start = new { s = m.Start.Sentence, o = m.Start.Offset },
            end = new { s = m.End.Sentence, o = m.End.Offset },
            style = m.Style.ToString().ToLowerInvariant(),
            note = !string.IsNullOrWhiteSpace(m.Note),
        });
        var json = JsonSerializer.Serialize(list);
        return CallAsync($"window.yy && window.yy.setMarks({json});");
    }

    public Task SetHitsAsync(string? query, int? sentence = null, int? nth = null)
    {
        if (string.IsNullOrEmpty(query))
            return CallAsync("window.yy && window.yy.setHits(null);");
        var q = JsonSerializer.Serialize(query);
        var cur = sentence is int s && nth is int n ? $"{{ s: {s}, nth: {n} }}" : "null";
        return CallAsync($"window.yy && window.yy.setHits({q}, {cur});");
    }

    public Task ClearSelectionAsync() =>
        CallAsync("window.yy && window.yy.clearSelection();");

    public Task SelectRangeAsync(ReaderPoint start, ReaderPoint end) =>
        CallAsync($"window.yy && window.yy.select({{ s: {start.S}, o: {start.O} }}, {{ s: {end.S}, o: {end.O} }});");

    public async Task<ReaderRect?> MarkRectAsync(string id)
    {
        try
        {
            var escaped = JsonSerializer.Serialize(id);
            var raw = await CallAsync($"window.yy ? JSON.stringify(window.yy.markRect({escaped})) : 'null'");
            string? unescaped = JsonSerializer.Deserialize<string>(raw);
            if (string.IsNullOrWhiteSpace(unescaped) || unescaped == "null") return null;
            return JsonSerializer.Deserialize<ReaderRect>(unescaped, ReaderMessage.JsonOptions);
        }
        catch { return null; }
    }

    public Task SetLookupAsync(bool on) =>
        CallAsync($"window.yy && window.yy.setLookup({(on ? "true" : "false")});");

    public Task SetVocabAsync(IEnumerable<string> words)
    {
        var json = JsonSerializer.Serialize(words);
        return CallAsync($"window.yy && window.yy.setVocab({json});");
    }

    public Task SetGlossesAsync(IEnumerable<Gloss> glosses)
    {
        var list = glosses.Select(g => new { form = g.Form, gloss = g.GlossValue });
        var json = JsonSerializer.Serialize(list);
        return CallAsync($"window.yy && window.yy.setGlosses({json});");
    }

    public Task SetLookingAsync(ReaderPoint? start, ReaderPoint? end)
    {
        if (start is null || end is null)
            return CallAsync("window.yy && window.yy.setLooking(null, null);");
        return CallAsync($"window.yy && window.yy.setLooking({{ s: {start.S}, o: {start.O} }}, {{ s: {end.S}, o: {end.O} }});");
    }

    public sealed record ForeignParagraphDto(int first, int last, string text);

    public async Task<IReadOnlyList<ForeignParagraphDto>> GetForeignParagraphsAsync()
    {
        try
        {
            var raw = await CallAsync("window.yy ? JSON.stringify(window.yy.paragraphs()) : '[]'");
            string? unescaped = JsonSerializer.Deserialize<string>(raw);
            if (string.IsNullOrWhiteSpace(unescaped)) return Array.Empty<ForeignParagraphDto>();
            return JsonSerializer.Deserialize<ForeignParagraphDto[]>(unescaped) ?? Array.Empty<ForeignParagraphDto>();
        }
        catch
        {
            return Array.Empty<ForeignParagraphDto>();
        }
    }

    public Task SetTranslationsAsync(IEnumerable<(int Last, string Text)>? items, bool only = false)
    {
        // reader/src/main.ts:320-323 already runs relayout() inside setTranslations; no extra applySettings needed.
        if (items is null)
            return CallAsync("window.yy && window.yy.setTranslations(null);");
        var list = items.Select(x => new { last = x.Last, text = x.Text });
        var json = JsonSerializer.Serialize(list);
        return CallAsync($"window.yy && window.yy.setTranslations({json}, {(only ? "true" : "false")});");
    }

    /// <summary>Switches already-rendered translations between bilingual and translation-only without re-sending them.</summary>
    public Task SetTranslationOnlyAsync(bool only) =>
        CallAsync($"window.yy && window.yy.setTranslationOnly && window.yy.setTranslationOnly({(only ? "true" : "false")});");

    /// <summary>
    /// Saves a PNG snapshot of the currently rendered page for instant-open preview (TECH.md §3).
    /// </summary>
    public async Task CapturePreviewPngAsync(string filePath)
    {
        if (!_initialized || _webView.CoreWebView2 is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            using var mem = new InMemoryRandomAccessStream();
            await _webView.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, mem);
            if (mem.Size == 0) return;
            mem.Seek(0);
            var bytes = new byte[mem.Size];
            await mem.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
            string tmp = filePath + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes);
            File.Move(tmp, filePath, overwrite: true);
        }
        catch { }
    }

    private void OnWebResourceRequested(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        if (!Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var uri)) return;
        var path = uri.AbsolutePath;
        var env = sender.Environment;

        // 1. Reader bundle (/app/reader.js, /app/reader.css)
        if (path.StartsWith("/app/", StringComparison.Ordinal))
        {
            var rel = path["/app/".Length..];
            if (rel.Contains("..")) { args.Response = NotFound(env); return; }
            var full = Path.Combine(_readerAssetsDir, rel.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(full))
            {
                var mime = rel.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
                    ? "text/javascript; charset=utf-8"
                    : rel.EndsWith(".css", StringComparison.OrdinalIgnoreCase)
                        ? "text/css; charset=utf-8"
                        : "application/octet-stream";
                args.Response = FromBytes(env, File.ReadAllBytes(full), mime);
                return;
            }
            args.Response = NotFound(env);
            return;
        }

        // 2. Downloaded custom fonts (/fonts/<file>)
        if (path.StartsWith("/fonts/", StringComparison.Ordinal))
        {
            var rel = Uri.UnescapeDataString(path["/fonts/".Length..]);
            if (rel.Contains("..") || rel.Contains('/') || rel.Contains('\\'))
            {
                args.Response = NotFound(env);
                return;
            }
            var full = Path.Combine(_library.FontsDir, rel);
            if (File.Exists(full))
            {
                var mime = rel.EndsWith(".woff2", StringComparison.OrdinalIgnoreCase)
                    ? "font/woff2"
                    : rel.EndsWith(".otf", StringComparison.OrdinalIgnoreCase)
                        ? "font/otf"
                        : rel.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase)
                            ? "font/collection"
                            : "font/ttf";
                args.Response = FromBytes(env, File.ReadAllBytes(full), mime);
                return;
            }
            args.Response = NotFound(env);
            return;
        }

        // 3. Book section HTML & embedded resources (/book/<id>/s/<n>.html, /book/<id>/r/<path>)
        if (path.StartsWith("/book/", StringComparison.Ordinal))
        {
            var served = _library.Serve(path, _geometryProvider());
            switch (served)
            {
                case Served.Html html:
                    string body = BuildSectionHtml(path, html.Body);
                    args.Response = FromBytes(env, Encoding.UTF8.GetBytes(body), "text/html; charset=utf-8");
                    return;
                case Served.File file when File.Exists(file.Path):
                    args.Response = FromBytes(env, File.ReadAllBytes(file.Path), file.Mime);
                    return;
                default:
                    args.Response = NotFound(env);
                    return;
            }
        }

        args.Response = NotFound(env);
    }

    private string BuildSectionHtml(string path, string body)
    {
        body = InjectWoff2FontFaces(body);

        if (_library.Prefs.GlobalEinkMode)
        {
            body = PaperAppearance.ApplyToHtml(body, "eink", null, null);
            int start = body.IndexOf("<html ", StringComparison.Ordinal);
            if (start >= 0)
            {
                int end = body.IndexOf('>', start);
                if (end > start)
                {
                    string tag = body[start..end];
                    tag = Regex.Replace(tag, @" data-yy-eink=""[^""]*""", "");
                    tag = Regex.Replace(tag, @" data-yy-eink-gray=""[^""]*""", "");
                    string grayVal = _library.Prefs.GlobalEinkGray ? "1" : "0";
                    tag += $@" data-yy-eink=""1"" data-yy-eink-gray=""{grayVal}""";
                    body = body[..start] + tag + body[end..];
                }
            }
            return body;
        }

        if (_library.Prefs.PaperFollowAppearance)
        {
            var segs = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            string? bookId = segs.Length >= 2 ? Uri.UnescapeDataString(segs[1]) : null;
            try
            {
                var rs = (bookId is not null ? _library.GetBookReaderSettings(bookId) : null)
                    ?? _library.GetReaderSettings();
                var resolved = PaperAppearance.Resolve(_library, rs);
                if (resolved != PaperAppearance.Raw(_library, rs))
                    body = PaperAppearance.ApplyToHtml(body, resolved);
            }
            catch { }
        }

        return body;
    }

    private string InjectWoff2FontFaces(string body)
    {
        try
        {
            if (!Directory.Exists(_library.FontsDir)) return body;
            StringBuilder? css = null;
            foreach (string full in Directory.EnumerateFiles(_library.FontsDir, "*.woff2"))
            {
                string name = Path.GetFileName(full);
                if (string.IsNullOrEmpty(name) || name.StartsWith('.') || name.Contains('"') || name.Contains('\''))
                    continue;
                var clean = new StringBuilder(name.Length);
                foreach (char ch in name)
                {
                    if (ch is not ('"' or '\\' or '<' or '>' or ';' or '{' or '}'))
                        clean.Append(ch);
                }
                css ??= new StringBuilder();
                css.Append($"@font-face{{font-family:\"yy-file:{clean}\";src:url(\"/fonts/{Uri.EscapeDataString(name)}\");font-display:swap}}");
            }
            if (css is not null)
            {
                int headEnd = body.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
                if (headEnd >= 0)
                    body = body.Insert(headEnd, $"<style>{css}</style>\n");
            }
        }
        catch { }
        return body;
    }

    private static CoreWebView2WebResourceResponse FromBytes(CoreWebView2Environment env, byte[] bytes, string contentType)
    {
        var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(bytes);
            writer.StoreAsync().AsTask().GetAwaiter().GetResult();
            writer.DetachStream();
        }
        stream.Seek(0);
        return env.CreateWebResourceResponse(stream, 200, "OK", $"Content-Type: {contentType}\r\nCache-Control: no-store");
    }

    private static CoreWebView2WebResourceResponse NotFound(CoreWebView2Environment env) =>
        env.CreateWebResourceResponse(null, 404, "Not Found", "Content-Type: text/plain");

    private void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        // Guard against stale messages from a previous navigation generation
        if (Uri.TryCreate(args.Source, UriKind.Absolute, out var srcUri))
        {
            var q = srcUri.Query;
            int gIdx = q.IndexOf("g=", StringComparison.Ordinal);
            if (gIdx >= 0 && long.TryParse(q[(gIdx + 2)..].Split('&')[0], out long msgGen) && msgGen != _generation)
                return;
        }

        var msg = ReaderMessage.Parse(args.WebMessageAsJson);
        if (msg is null) return;
        if (msg.Type == "log")
        {
            System.Diagnostics.Debug.WriteLine($"[ReaderJS/{msg.Level}] {msg.Message}");
            return;
        }
        MessageReceived?.Invoke(msg);
    }

    private static void OnNewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
    }

    public void Dispose()
    {
        if (_initialized && _webView.CoreWebView2 is not null)
        {
            try
            {
                _webView.CoreWebView2.WebResourceRequested -= OnWebResourceRequested;
                _webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
                _webView.CoreWebView2.NewWindowRequested -= OnNewWindowRequested;
            }
            catch { }
        }
        _initialized = false;
        try { _webView.Close(); } catch { }
    }
}
