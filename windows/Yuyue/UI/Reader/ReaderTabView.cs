using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;
using Yuyue.Core;
using Yuyue.UI.Controls;
using Yuyue.UI.Shell;

namespace Yuyue.UI.Reader;

/// <summary>
/// Tabbed Reader host (<see cref="ITabContent"/>) supporting both single-book reading AND
/// Windows 11 in-tab **Dual-Book Split View** (primary + optional secondary <see cref="ReaderPaneView"/>
/// separated by a smooth draggable <see cref="PaneSplitter"/>).
/// </summary>
public sealed class ReaderTabView : UserControl, ITabContent
{
    private MainWindow _ownerWindow;
    private readonly LibraryService _library;
    private readonly Grid _rootGrid;
    private readonly ColumnDefinition _primaryCol;
    private readonly ColumnDefinition _splitterCol;
    private readonly ColumnDefinition _secondaryCol;
    private readonly PaneSplitter _splitter;
    private readonly ReaderPaneView _primaryPane;
    private ReaderPaneView? _secondaryPane;
    private ReaderPaneView _activePane;
    private bool _disposed;

    public string TabId { get; }
    public string TabTitle => _secondaryPane is not null
        ? $"{_primaryPane.BookTitle} | {_secondaryPane.BookTitle}"
        : _primaryPane.BookTitle;
    public string? BookId => _primaryPane.BookId;
    public string TabKind => "reader";
    public string TabIconName => "book-open";
    public bool IsPinned => false;
    public bool IsAudioPlaying => _primaryPane.IsAudioPlaying || (_secondaryPane?.IsAudioPlaying == true);
    public PaperChromeTheme? ActivePaperChrome => _primaryPane.CurrentPaperChrome;
    public UIElement ViewElement => this;
    public event Action? TabMetaChanged;

    public ReaderPaneView PrimaryPane => _primaryPane;

    public ReaderTabView(
        MainWindow ownerWindow,
        LibraryService library,
        string bookId,
        bool autoListen = false,
        uint? section = null,
        int? sentence = null,
        string? askQuestion = null,
        string? searchQuery = null,
        string? splitBookId = null)
    {
        _ownerWindow = ownerWindow;
        _library = library;
        TabId = $"reader:{bookId}:{Guid.NewGuid():N}";

        _rootGrid = new Grid();
        _primaryCol = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
        _splitterCol = new ColumnDefinition { Width = new GridLength(0) };
        _secondaryCol = new ColumnDefinition { Width = new GridLength(0) };
        _rootGrid.ColumnDefinitions.Add(_primaryCol);
        _rootGrid.ColumnDefinitions.Add(_splitterCol);
        _rootGrid.ColumnDefinitions.Add(_secondaryCol);

        _primaryPane = new ReaderPaneView(
            ownerWindow, library, bookId,
            isSecondarySplitPane: false,
            autoListen: autoListen,
            initialSection: section,
            initialSentence: sentence,
            initialAskQuestion: askQuestion,
            initialSearchQuery: searchQuery);
        _primaryPane.SetOwnerTab(this);
        _activePane = _primaryPane;
        _primaryPane.GotFocus += (_, _) => _activePane = _primaryPane;
        _primaryPane.PointerEntered += (_, _) => _activePane = _primaryPane;
        _primaryPane.PaneActivated += () => _activePane = _primaryPane;

        _primaryPane.ChromeOrMetaChanged += () => TabMetaChanged?.Invoke();
        _primaryPane.RequestOpenSplitBook += OpenSplitBook;
        _primaryPane.RequestDetachWindow += () => App.CurrentApp.TearOutTabToNewWindow(_ownerWindow, this);

        _splitter = new PaneSplitter { Visibility = Visibility.Collapsed };
        _splitter.DragDelta += dx =>
        {
            double totalW = _rootGrid.ActualWidth > 0 ? _rootGrid.ActualWidth : 600;
            double minPane = Math.Min(340, Math.Max(120, (totalW - 6) / 2.0));
            double maxPane = Math.Max(minPane, totalW - minPane);
            double curPrimary = _primaryPane.ActualWidth > 0 ? _primaryPane.ActualWidth : totalW * 0.5;
            double nextPrimary = Math.Clamp(curPrimary + dx, minPane, maxPane);
            _primaryCol.Width = new GridLength(nextPrimary, GridUnitType.Star);
            _secondaryCol.Width = new GridLength(Math.Max(minPane, totalW - nextPrimary), GridUnitType.Star);
        };

        Grid.SetColumn(_primaryPane, 0);
        Grid.SetColumn(_splitter, 1);
        _rootGrid.Children.Add(_primaryPane);
        _rootGrid.Children.Add(_splitter);
        Content = _rootGrid;

        if (!string.IsNullOrWhiteSpace(splitBookId) && splitBookId != bookId)
            OpenSplitBook(splitBookId);
    }

    public void OpenSplitBook(string secondBookId)
    {
        CloseSplitBook();
        try
        {
            var sec = new ReaderPaneView(_ownerWindow, _library, secondBookId, isSecondarySplitPane: true);
            sec.SetOwnerTab(this);
            sec.GotFocus += (_, _) => _activePane = sec;
            sec.PointerEntered += (_, _) => _activePane = sec;
            sec.PaneActivated += () => _activePane = sec;
            sec.RequestCloseSplitPane += CloseSplitBook;
            sec.ChromeOrMetaChanged += () => TabMetaChanged?.Invoke();
            _secondaryPane = sec;

            Grid.SetColumn(sec, 2);
            _rootGrid.Children.Add(sec);
            _splitter.Visibility = Visibility.Visible;
            _primaryCol.Width = new GridLength(1, GridUnitType.Star);
            _splitterCol.Width = new GridLength(6);
            _secondaryCol.Width = new GridLength(1, GridUnitType.Star);
            TabMetaChanged?.Invoke();
        }
        catch { }
    }

    public void CloseSplitBook()
    {
        if (_secondaryPane is null) return;
        _rootGrid.Children.Remove(_secondaryPane);
        try { _secondaryPane.Dispose(); } catch { }
        _secondaryPane = null;
        _activePane = _primaryPane;
        _splitter.Visibility = Visibility.Collapsed;
        _primaryCol.Width = new GridLength(1, GridUnitType.Star);
        _splitterCol.Width = new GridLength(0);
        _secondaryCol.Width = new GridLength(0);
        TabMetaChanged?.Invoke();
    }

    public void OnTabActivated() => _primaryPane.ApplyPaperAndChrome();
    public void OnTabDeactivated() { }
    public void OnAppearanceChanged()
    {
        _primaryPane.OnAppearanceChanged();
        _secondaryPane?.OnAppearanceChanged();
    }
    public void OnReaderSettingsChanged()
    {
        _primaryPane.ReloadReaderSettingsFromCore();
        _secondaryPane?.ReloadReaderSettingsFromCore();
    }
    public void OnVoiceChanged()
    {
        _primaryPane.ReloadVoicePrefs();
        _secondaryPane?.ReloadVoicePrefs();
    }
    public void OnForeignPrefsChanged()
    {
        _primaryPane.ReloadForeignPrefs();
        _secondaryPane?.ReloadForeignPrefs();
    }
    public void OnSnapshotRestored()
    {
        OnAppearanceChanged();
        OnReaderSettingsChanged();
    }

    private ReaderPaneView ActivePane =>
        _secondaryPane is not null && ReferenceEquals(_activePane, _secondaryPane)
            ? _secondaryPane
            : _primaryPane;

    public bool HandleWindowKeyDown(VirtualKey key, bool ctrl, bool shift, bool alt) =>
        ActivePane.HandleWindowKeyDown(key, ctrl, shift, alt);

    public bool HandleMouseBackForward(bool isBack) =>
        ActivePane.HandleMouseBackForward(isBack);

    public void SetFullScreenChrome(bool isFullScreen)
    {
        _primaryPane.SetFullScreenChrome(isFullScreen);
        _secondaryPane?.SetFullScreenChrome(isFullScreen);
    }

    public void SetOwnerWindow(MainWindow owner)
    {
        _ownerWindow = owner;
        _primaryPane.SetOwnerWindow(owner);
        _secondaryPane?.SetOwnerWindow(owner);
    }

    public bool ContainsBookId(string bookId) =>
        string.Equals(_primaryPane.BookId, bookId, StringComparison.OrdinalIgnoreCase)
        || (_secondaryPane is not null && string.Equals(_secondaryPane.BookId, bookId, StringComparison.OrdinalIgnoreCase));

    public SavedTabEntry? SerializeTabState() =>
        new(_primaryPane.BookId, "reader", _secondaryPane?.BookId);

    public Task<bool> PrepareCloseAsync() => Task.FromResult(true);

    public IEnumerable<(UIElement HostElement, Yuyue.Reader.ReaderWebViewHost WebViewHost)> GetWebViewCaptureTargets()
    {
        var (el1, wv1) = _primaryPane.GetWebViewCaptureTarget();
        if (wv1 is not null) yield return (el1, wv1);
        if (_secondaryPane is not null)
        {
            var (el2, wv2) = _secondaryPane.GetWebViewCaptureTarget();
            if (wv2 is not null) yield return (el2, wv2);
        }
    }

    public Task<bool> SaveCompositeScreenshotAsync(string path) =>
        _primaryPane.SaveCompositeScreenshotAsync(path);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CloseSplitBook();
        _primaryPane.Dispose();
    }
}
