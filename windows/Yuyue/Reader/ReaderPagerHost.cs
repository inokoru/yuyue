using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Yuyue.Core;

namespace Yuyue.Reader;

/// <summary>
/// 3-slot adjacent-chapter WebView2 pager aligned with <c>TECH.md §3</c> and <c>apple/Yuyue/Reader/ReaderPager.swift</c>.
/// Keeps <c>prev</c>, <c>current</c>, and <c>next</c> sections preloaded in pooled <see cref="WebView2"/> instances
/// sharing one <see cref="Microsoft.Web.WebView2.Core.CoreWebView2Environment"/>, so turning across chapter
/// boundaries swaps instantly without navigation flash.
/// </summary>
public sealed class ReaderPagerHost : IDisposable
{
    private sealed class Slot
    {
        public required WebView2 View { get; init; }
        public required ReaderWebViewHost Host { get; init; }
        public uint? Section { get; set; }
        public bool IsReady { get; set; }
        public bool JoinAtEnd { get; set; }
        public object? PendingSettings { get; set; }
        public SectionLayout? Layout { get; set; }
    }

    private readonly Grid _container;
    private readonly LibraryService _library;
    private readonly string _bookId;
    private readonly Func<PageGeometry> _geometryProvider;

    private readonly List<Slot> _pool = [];
    private Slot? _current;
    private object? _lastSettingsPayload;
    private bool _disposed;

    public bool IsInitialized => _current is not null;
    public ReaderWebViewHost Current => _current?.Host ?? throw new InvalidOperationException("ReaderPagerHost not initialized");
    public WebView2? CurrentWebView => _current?.View;
    public SectionLayout? CurrentLayout => _current?.Layout;

    public event Action<ReaderMessage>? MessageReceived;

    public ReaderPagerHost(Grid container, LibraryService library, string bookId, Func<PageGeometry> geometryProvider)
    {
        _container = container;
        _library = library;
        _bookId = bookId;
        _geometryProvider = geometryProvider;
    }

    public async Task InitializeAsync()
    {
        if (_current is not null) return;
        _current = await CreateSlotAsync();
        SetActiveSlot(_current);
    }

    private async Task<Slot> CreateSlotAsync()
    {
        var wv = new WebView2
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            DefaultBackgroundColor = Colors.Transparent,
            Opacity = 0,
            IsHitTestVisible = false,
        };
        Canvas.SetZIndex(wv, 0);
        _container.Children.Insert(0, wv);

        var host = new ReaderWebViewHost(wv, _library, _geometryProvider);
        var slot = new Slot { View = wv, Host = host };
        host.MessageReceived += msg => OnSlotMessage(slot, msg);
        await host.InitializeAsync();
        if (_disposed)
        {
            host.Dispose();
            throw new ObjectDisposedException(nameof(ReaderPagerHost));
        }
        _pool.Add(slot);
        return slot;
    }

    private void SetActiveSlot(Slot active)
    {
        _current = active;
        foreach (var s in _pool)
        {
            bool isCur = ReferenceEquals(s, active);
            Canvas.SetZIndex(s.View, isCur ? 1 : 0);
            s.View.Opacity = isCur ? 1.0 : 0.0;
            s.View.IsHitTestVisible = isCur;
        }
    }

    /// <summary>
    /// Opens <paramref name="section"/>. If already preloaded in a neighbor slot and <paramref name="forceReload"/>
    /// is false, swaps it into view immediately and fires a synthetic <c>ready</c> message for zero-flash chapter turns!
    /// </summary>
    public bool OpenSection(uint section, bool forceReload = false, bool joinAtEnd = false)
    {
        if (_disposed || _current is null) return false;

        if (!forceReload)
        {
            var preloaded = _pool.FirstOrDefault(s => !ReferenceEquals(s, _current) && s.Section == section);
            if (preloaded is not null)
            {
                var prevSlot = _current;
                if (prevSlot.IsReady)
                    _ = prevSlot.Host.ClearTransientOverlaysAsync();

                preloaded.JoinAtEnd = joinAtEnd;
                SetActiveSlot(preloaded);
                if (preloaded.IsReady && preloaded.Layout is { } lay)
                {
                    _ = preloaded.Host.ClearTransientOverlaysAsync();
                    if (preloaded.PendingSettings is { } pending)
                    {
                        preloaded.PendingSettings = null;
                        _ = preloaded.Host.ApplySettingsAsync(pending);
                    }
                    if (joinAtEnd)
                        _ = preloaded.Host.ScrollToEndOfSectionAsync(lay.PageCount);
                    else
                        _ = preloaded.Host.ScrollToStartOfSectionAsync();

                    MessageReceived?.Invoke(new ReaderMessage
                    {
                        Type = "ready",
                        Section = section,
                        PageCount = lay.PageCount,
                        SentencePages = lay.SentencePages,
                        PageFirstSentence = lay.PageFirstSentence,
                    });
                    return true;
                }

                preloaded.PendingSettings ??= _lastSettingsPayload;
                return false;
            }
        }

        // Fresh load or forced reload (e.g. replace rules / script / font pack changed): clear neighbor caches
        foreach (var s in _pool.Where(s => !ReferenceEquals(s, _current)))
        {
            s.Section = null;
            s.IsReady = false;
            s.Layout = null;
            s.PendingSettings = null;
            s.Host.ClearBlank();
        }

        _current.Section = section;
        _current.IsReady = false;
        _current.Layout = null;
        _current.JoinAtEnd = joinAtEnd;
        _current.PendingSettings = _lastSettingsPayload;
        _current.Host.LoadSection(_bookId, section);
        return false;
    }

    /// <summary>
    /// Preloads <c>previousSection</c> (joined to its last page) and <c>nextSection</c> (joined to page 0)
    /// into idle pooled WebView2 slots after the current section settles.
    /// </summary>
    public async Task PreloadNeighborsAsync(uint currentSection)
    {
        if (_disposed || _current is null) return;
        uint? prev = _library.PreviousSection(_bookId, currentSection);
        uint? next = _library.NextSection(_bookId, currentSection);

        var targets = new List<(uint Sec, bool AtEnd)>();
        if (prev is uint p) targets.Add((p, true));
        if (next is uint n) targets.Add((n, false));

        var wantedSet = targets.Select(t => t.Sec).ToHashSet();
        wantedSet.Add(currentSection);

        // Recycle any slot not in {prev, current, next}
        foreach (var s in _pool.Where(s => !ReferenceEquals(s, _current) && (s.Section is null || !wantedSet.Contains(s.Section.Value))))
        {
            s.Section = null;
            s.IsReady = false;
            s.Layout = null;
            s.PendingSettings = null;
        }

        foreach (var (sec, atEnd) in targets)
        {
            if (_disposed) return;
            var existing = _pool.FirstOrDefault(s => !ReferenceEquals(s, _current) && s.Section == sec);
            if (existing is not null)
            {
                existing.JoinAtEnd = atEnd;
                if (existing.IsReady && existing.Layout is { } lay)
                {
                    if (existing.PendingSettings is { } pending)
                    {
                        existing.PendingSettings = null;
                        _ = existing.Host.ApplySettingsAsync(pending);
                    }
                    if (atEnd)
                        _ = existing.Host.ScrollToEndOfSectionAsync(lay.PageCount);
                    else
                        _ = existing.Host.ScrollToStartOfSectionAsync();
                }
                else
                {
                    existing.PendingSettings ??= _lastSettingsPayload;
                }
                continue;
            }

            var free = _pool.FirstOrDefault(s => !ReferenceEquals(s, _current) && s.Section is null);
            if (free is null && _pool.Count < 3)
            {
                try { free = await CreateSlotAsync(); }
                catch { continue; }
            }
            if (_disposed || free is null) return;

            free.Section = sec;
            free.IsReady = false;
            free.Layout = null;
            free.JoinAtEnd = atEnd;
            free.PendingSettings = _lastSettingsPayload;
            free.Host.LoadSection(_bookId, sec);
        }
    }

    /// <summary>
    /// Applies settings to the active slot AND any preloaded neighbor slots so chapter flips stay visually identical.
    /// In-flight unready slots store <see cref="Slot.PendingSettings"/> and apply them immediately on <c>ready</c>.
    /// </summary>
    public Task ApplySettingsAllAsync(object settingsPayload)
    {
        _lastSettingsPayload = settingsPayload;
        var tasks = new List<Task>();
        foreach (var s in _pool)
        {
            if (s.Host.IsInitialized && (ReferenceEquals(s, _current) || s.IsReady))
            {
                s.PendingSettings = null;
                tasks.Add(s.Host.ApplySettingsAsync(settingsPayload));
            }
            else if (s.Section is not null)
            {
                s.PendingSettings = settingsPayload;
            }
        }
        return Task.WhenAll(tasks);
    }

    private void OnSlotMessage(Slot slot, ReaderMessage msg)
    {
        if (_disposed) return;
        if (msg.Type is "ready" or "layout")
        {
            slot.Layout = new SectionLayout(msg.PageCount ?? 1, msg.SentencePages, msg.PageFirstSentence);
            if (msg.Type == "ready") slot.IsReady = true;
        }

        if (ReferenceEquals(slot, _current))
        {
            if (msg.Type == "ready" && slot.PendingSettings is { } pending)
            {
                slot.PendingSettings = null;
                _ = slot.Host.ApplySettingsAsync(pending);
            }
            MessageReceived?.Invoke(msg);
        }
        else if (msg.Type is "ready" or "layout" && slot.Layout is { } lay)
        {
            // Keep preloaded neighbor synced with latest settings and positioned on its joining edge
            if (msg.Type == "ready")
            {
                var payload = slot.PendingSettings ?? _lastSettingsPayload;
                slot.PendingSettings = null;
                if (payload is not null)
                    _ = slot.Host.ApplySettingsAsync(payload);
                _ = slot.Host.ClearTransientOverlaysAsync();
            }
            if (slot.JoinAtEnd)
                _ = slot.Host.ScrollToEndOfSectionAsync(lay.PageCount);
            else
                _ = slot.Host.ScrollToStartOfSectionAsync();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var s in _pool) s.Host.Dispose();
        _pool.Clear();
    }
}
