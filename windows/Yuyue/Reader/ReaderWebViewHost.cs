using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.Storage.Streams;
using Yuyue.Core;

namespace Yuyue.Reader;

/// <summary>
/// Hosts the shared TypeScript reader bundle inside WinUI 3's <see cref="WebView2"/>.
/// Serves <c>https://yuyue.local/app/*</c> from bundled reader assets and
/// <c>https://yuyue.local/book/*</c> directly from <see cref="YuyueCore.Serve"/> (zero temp files).
/// </summary>
public sealed class ReaderWebViewHost : IDisposable
{
    public const string Origin = "https://yuyue.local";

    private readonly WebView2 _webView;
    private readonly LibraryService _library;
    private readonly Func<PageGeometry> _geometryProvider;
    private readonly string _readerAssetsDir;

    private bool _initialized;

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

        var userDataDir = Path.Combine(_library.DataDir, "webview2");
        Directory.CreateDirectory(userDataDir);

        var env = await CoreWebView2Environment.CreateWithOptionsAsync(null, userDataDir, null);
        await _webView.EnsureCoreWebView2Async(env);

        var core = _webView.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
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

        // Windows ergonomics:
        // 1. Mouse wheel turns pages in paginated mode (standard Windows desktop expectation).
        // 2. Ctrl + Wheel zooms font size cleanly through native settings.
        // 3. Right-click forwards coordinates so WinUI 3 shows a native context MenuFlyout.
        await core.AddScriptToExecuteOnDocumentCreatedAsync(@"
            (() => {
                let lastWheel = 0;
                window.addEventListener('wheel', (e) => {
                    if (e.ctrlKey) {
                        e.preventDefault();
                        window.chrome?.webview?.postMessage({ type: 'wheel', deltaY: e.deltaY, ctrl: true });
                        return;
                    }
                    if (document.documentElement.dataset.yyMode !== 'scrolled' && Math.abs(e.deltaY) >= Math.abs(e.deltaX)) {
                        e.preventDefault();
                        const now = performance.now();
                        if (now - lastWheel < 200) return;
                        lastWheel = now;
                        window.chrome?.webview?.postMessage({ type: 'wheel', deltaY: e.deltaY, ctrl: false });
                    }
                }, { passive: false });
                window.addEventListener('contextmenu', (e) => {
                    e.preventDefault();
                    window.chrome?.webview?.postMessage({ type: 'winContext', x: e.clientX, y: e.clientY });
                });
            })();
        ");

        _initialized = true;
    }

    public void LoadSection(string bookId, uint section)
    {
        if (!_initialized) return;
        _webView.CoreWebView2.Navigate($"{Origin}/book/{bookId}/s/{section}.html");
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

    public Task ScrollToSentenceAsync(int sentence, bool animated = false) =>
        CallAsync($"window.yy && window.yy.scrollToSentence({sentence}, {(animated ? "true" : "false")});");

    public Task ScrollToAnchorAsync(string anchor, bool animated = false)
    {
        var escaped = JsonSerializer.Serialize(anchor);
        return CallAsync($"window.yy && window.yy.scrollToAnchor({escaped}, {(animated ? "true" : "false")});");
    }

    public Task SetActiveAsync(int? sentence, int durationMs = 0, int elapsedMs = 0, bool playing = true)
    {
        if (sentence is null)
        {
            return CallAsync("window.yy && window.yy.setActive(null);");
        }
        return CallAsync($"window.yy && window.yy.setActive({sentence.Value}, {{ durationMs: {durationMs}, elapsedMs: {elapsedMs}, playing: {(playing ? "true" : "false")} }});");
    }

    public Task SetPlayingAsync(bool playing) =>
        CallAsync($"window.yy && window.yy.setPlaying({(playing ? "true" : "false")});");

    public Task SetMarksAsync(IEnumerable<Mark> marks)
    {
        var list = marks.Select(m => new
        {
            id = m.Id,
            start = new { s = m.Start.Sentence, o = m.Start.Offset },
            end = new { s = m.End.Sentence, o = m.End.Offset },
            style = m.Style.ToString().ToLowerInvariant(),
            note = m.Note,
        });
        var json = JsonSerializer.Serialize(list);
        return CallAsync($"window.yy && window.yy.setMarks({json});");
    }

    public Task SetHitsAsync(string? query, int? sentence = null, int? nth = null)
    {
        if (string.IsNullOrEmpty(query))
        {
            return CallAsync("window.yy && window.yy.setHits(null);");
        }
        var q = JsonSerializer.Serialize(query);
        var cur = sentence is int s && nth is int n ? $"{{ s: {s}, nth: {n} }}" : "null";
        return CallAsync($"window.yy && window.yy.setHits({q}, {cur});");
    }

    public Task ClearSelectionAsync() =>
        CallAsync("window.yy && window.yy.clearSelection();");

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
            // ExecuteScriptAsync returns a JSON-encoded string literal
            string? unescaped = JsonSerializer.Deserialize<string>(raw);
            if (string.IsNullOrWhiteSpace(unescaped)) return Array.Empty<ForeignParagraphDto>();
            return JsonSerializer.Deserialize<ForeignParagraphDto[]>(unescaped) ?? Array.Empty<ForeignParagraphDto>();
        }
        catch
        {
            return Array.Empty<ForeignParagraphDto>();
        }
    }

    public Task SetTranslationsAsync(IEnumerable<(int Last, string Text)>? items)
    {
        if (items is null)
            return CallAsync("if (window.yy) { window.yy.setTranslations(null); window.yy.applySettings({}); }");
        var list = items.Select(x => new { last = x.Last, text = x.Text });
        var json = JsonSerializer.Serialize(list);
        return CallAsync($"if (window.yy) {{ window.yy.setTranslations({json}); window.yy.applySettings({{}}); }}");
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
                    args.Response = FromBytes(env, Encoding.UTF8.GetBytes(html.Body), "text/html; charset=utf-8");
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
        var msg = ReaderMessage.Parse(args.WebMessageAsJson);
        if (msg is not null)
        {
            MessageReceived?.Invoke(msg);
        }
    }

    private static void OnNewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
    }

    public void Dispose()
    {
        if (!_initialized || _webView.CoreWebView2 is null) return;
        _webView.CoreWebView2.WebResourceRequested -= OnWebResourceRequested;
        _webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
        _webView.CoreWebView2.NewWindowRequested -= OnNewWindowRequested;
    }
}
