using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Yuyue.Core;
using Yuyue.Design;

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
        public bool HtmlReady { get; set; }
        public bool IsReady { get; set; }
        public bool JoinAtEnd { get; set; }
        public object? PendingSettings { get; set; }
        public SectionLayout? Layout { get; set; }
        public TaskCompletionSource<bool>? LoadReadyTcs { get; set; }
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

    /// <summary>
    /// Optional decorator invoked on preloaded neighbor slots right after <c>ApplySettingsAsync</c>
    /// so bubbles, marks, vocab, glosses, and cached translations settle before <c>IsReady</c> and <c>JoinAtEnd</c> page calculation.
    /// </summary>
    public Func<ReaderWebViewHost, uint, Task>? DecorateSectionBeforeReadyAsync { get; set; }

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
            IsTabStop = false,
            AllowFocusOnInteraction = false,
        };
        Canvas.SetZIndex(wv, -1);
        AutomationProperties.SetAccessibilityView(wv, AccessibilityView.Raw);
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
        if (_current is not null && !ReferenceEquals(slot, _current))
            wv.Visibility = Visibility.Collapsed;
        _pool.Add(slot);
        return slot;
    }

    private void SetActiveSlot(Slot active)
    {
        _current = active;
        foreach (var s in _pool)
        {
            bool isCur = ReferenceEquals(s, active);
            s.View.Visibility = isCur ? Visibility.Visible : Visibility.Collapsed;
            s.View.Opacity = isCur ? 1.0 : 0.0;
            s.View.IsHitTestVisible = isCur;
            s.View.IsTabStop = isCur;
            s.View.AllowFocusOnInteraction = isCur;
            Canvas.SetZIndex(s.View, isCur ? 1 : -1);
            AutomationProperties.SetAccessibilityView(s.View, isCur ? AccessibilityView.Content : AccessibilityView.Raw);
        }

        FocusActiveWebViewIfSafe(active.View);
    }

    private void FocusActiveWebViewIfSafe(WebView2? view)
    {
        if (_disposed || view is null) return;
        var xamlRoot = _container.XamlRoot;
        if (xamlRoot is null) return;
        if (FocusManager.GetFocusedElement(xamlRoot) is TextBox) return;
        if (YYChrome.HasBlockingModalOrFlyoutOpen(xamlRoot)) return;
        try { view.Focus(FocusState.Programmatic); } catch { }
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
                if (prevSlot.IsReady || prevSlot.HtmlReady)
                    _ = prevSlot.Host.ClearTransientOverlaysAsync();

                preloaded.JoinAtEnd = joinAtEnd;
                SetActiveSlot(preloaded);
                if ((preloaded.IsReady || preloaded.HtmlReady) && preloaded.Layout is { } lay)
                {
                    preloaded.IsReady = true;
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
            s.LoadReadyTcs?.TrySetResult(false);
            s.LoadReadyTcs = null;
            s.Section = null;
            s.HtmlReady = false;
            s.IsReady = false;
            s.Layout = null;
            s.PendingSettings = null;
            s.Host.ClearBlank();
        }

        _current.LoadReadyTcs?.TrySetResult(false);
        _current.LoadReadyTcs = null;
        _current.Section = section;
        _current.HtmlReady = false;
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
            s.LoadReadyTcs?.TrySetResult(false);
            s.LoadReadyTcs = null;
            s.Section = null;
            s.HtmlReady = false;
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
                        await existing.Host.ApplySettingsAsync(pending);
                    }
                    if (DecorateSectionBeforeReadyAsync is not null)
                    {
                        try { await DecorateSectionBeforeReadyAsync(existing.Host, sec); } catch { }
                    }
                    if (_disposed || existing.Section != sec || ReferenceEquals(existing, _current)) continue;
                    var finalLay = existing.Layout ?? lay;
                    if (atEnd)
                        await existing.Host.ScrollToEndOfSectionAsync(finalLay.PageCount);
                    else
                        await existing.Host.ScrollToStartOfSectionAsync();
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

            await PreloadSlotAsync(free, sec, atEnd);
        }
    }

    private async Task PreloadSlotAsync(Slot slot, uint sec, bool atEnd)
    {
        slot.LoadReadyTcs?.TrySetResult(false);
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        slot.LoadReadyTcs = tcs;
        slot.Section = sec;
        slot.HtmlReady = false;
        slot.IsReady = false;
        slot.Layout = null;
        slot.JoinAtEnd = atEnd;
        slot.PendingSettings = _lastSettingsPayload;
        slot.View.Opacity = 0;
        slot.View.Visibility = Visibility.Visible;
        try
        {
            slot.Host.LoadSection(_bookId, sec);

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(8000));
            if (_disposed || completed != tcs.Task || !tcs.Task.IsCompletedSuccessfully || !tcs.Task.Result)
                return;
            if (slot.Section != sec || ReferenceEquals(slot, _current))
                return;

            try
            {
                var payload = slot.PendingSettings ?? _lastSettingsPayload;
                slot.PendingSettings = null;
                if (payload is not null)
                    await slot.Host.ApplySettingsAsync(payload);

                if (DecorateSectionBeforeReadyAsync is not null)
                    await DecorateSectionBeforeReadyAsync(slot.Host, sec);

                await slot.Host.ClearTransientOverlaysAsync();
                // Let reader.js flush any relayout() queued by settings/bubbles/glosses/translations
                await slot.Host.CallAsync("new Promise(r => requestAnimationFrame(() => requestAnimationFrame(() => r(0))))");
            }
            catch { }

            if (_disposed || slot.Section != sec || ReferenceEquals(slot, _current))
                return;

            if (slot.Layout is { } lay)
            {
                if (slot.JoinAtEnd)
                    await slot.Host.ScrollToEndOfSectionAsync(lay.PageCount);
                else
                    await slot.Host.ScrollToStartOfSectionAsync();
            }
            slot.IsReady = true;
        }
        finally
        {
            if (!_disposed && !ReferenceEquals(slot, _current))
                slot.View.Visibility = Visibility.Collapsed;
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
            if (msg.Type == "ready") slot.HtmlReady = true;
        }

        if (ReferenceEquals(slot, _current))
        {
            if (msg.Type == "ready")
            {
                slot.IsReady = true;
                slot.LoadReadyTcs?.TrySetResult(false);
                if (slot.PendingSettings is { } pending)
                {
                    slot.PendingSettings = null;
                    _ = slot.Host.ApplySettingsAsync(pending);
                }
            }
            MessageReceived?.Invoke(msg);
            return;
        }

        // Inactive slot: if a residual key/wheel event lands on an old Chromium HWND, still forward and restore focus to current!
        if (msg.Type is "winKey" or "wheel")
        {
            MessageReceived?.Invoke(msg);
            FocusActiveWebViewIfSafe(_current?.View);
            return;
        }

        if (msg.Type == "ready")
        {
            if (slot.LoadReadyTcs is { } tcs && tcs.TrySetResult(true))
                return;

            // Fallback if slot became ready outside PreloadSlotAsync
            var payload = slot.PendingSettings ?? _lastSettingsPayload;
            slot.PendingSettings = null;
            if (payload is not null)
                _ = slot.Host.ApplySettingsAsync(payload);
            _ = slot.Host.ClearTransientOverlaysAsync();
            slot.IsReady = true;
            if (slot.Layout is { } readyLay)
            {
                if (slot.JoinAtEnd)
                    _ = slot.Host.ScrollToEndOfSectionAsync(readyLay.PageCount);
                else
                    _ = slot.Host.ScrollToStartOfSectionAsync();
            }
        }
        else if (msg.Type == "layout" && slot.Layout is { } lay)
        {
            if (slot.JoinAtEnd)
                _ = slot.Host.ScrollToEndOfSectionAsync(lay.PageCount);
            else
                _ = slot.Host.ScrollToStartOfSectionAsync();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var s in _pool)
        {
            s.LoadReadyTcs?.TrySetResult(false);
            s.Host.Dispose();
        }
        _pool.Clear();
    }
}
