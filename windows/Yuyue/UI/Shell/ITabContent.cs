using Microsoft.UI.Xaml;
using Windows.System;
using Windows.UI;
using Yuyue.Core;

namespace Yuyue.UI.Shell;

/// <summary>
/// Active reading paper chrome palette broadcast by a Reader tab to <see cref="MainWindow"/>
/// so the 38px TabView titlebar strip and native Windows 11 caption buttons blend seamlessly into one continuous sheet of paper.
/// </summary>
public sealed record PaperChromeTheme(
    Color PaperBg,
    Color TabStripBg,
    Color Ink1,
    Color Ink2,
    Color Line,
    bool IsDark);

/// <summary>
/// Unified contract for every tab hosted inside <see cref="MainWindow"/>'s native WinUI 3 <c>TabView</c>.
/// </summary>
public interface ITabContent : IDisposable
{
    string TabId { get; }
    string TabTitle { get; }
    string? BookId { get; }
    string TabKind { get; } // library | reader | pdf | audiobook | podcast
    string TabIconName { get; }
    bool IsPinned { get; }
    bool IsAudioPlaying { get; }
    PaperChromeTheme? ActivePaperChrome { get; }
    UIElement ViewElement { get; }

    event Action? TabMetaChanged;

    void OnTabActivated();
    void OnTabDeactivated();
    void OnAppearanceChanged();
    void OnReaderSettingsChanged();
    void OnVoiceChanged();
    void OnForeignPrefsChanged();
    void OnSnapshotRestored();

    bool HandleWindowKeyDown(VirtualKey key, bool ctrl, bool shift, bool alt);
    bool HandleMouseBackForward(bool isBack);
    void SetFullScreenChrome(bool isFullScreen);
    void SetOwnerWindow(MainWindow owner) { }
    bool ContainsBookId(string bookId) => string.Equals(BookId, bookId, StringComparison.OrdinalIgnoreCase);
    SavedTabEntry? SerializeTabState();
    Task<bool> PrepareCloseAsync();
    Task<bool> SaveCompositeScreenshotAsync(string path);
}

/// <summary>
/// Unified playback controller exposed by any active audio surface (Reader TTS, PDF TTS, Audiobook, Podcast)
/// so <see cref="MiniPlayerWindow"/> and <see cref="Controls.TaskbarThumbToolbar"/> can control playback uniformly.
/// </summary>
public interface IMiniPlayerSource
{
    string SourceId { get; }
    string Title { get; }
    string Subtitle { get; }
    string? CoverPath { get; }
    bool IsPlaying { get; }
    double ProgressRatio { get; }
    double PlaybackRate { get; }

    event Action? StateChanged;

    void TogglePlayPause();
    void SkipPrevious();
    void SkipNext();
    void CycleRate();
    void JumpToSource();
}

/// <summary>
/// Clean host interface implemented by <c>LibraryView</c> so <c>PodcastWorkspaceBuilder</c>,
/// <c>BookRecordPageBuilder</c>, and library subviews never couple directly to any <c>Window</c> class.
/// </summary>
public interface ILibraryHost
{
    Window OwnerWindow { get; }
    Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue { get; }
    XamlRoot? WindowXamlRoot { get; }
    UIElement? Content { get; }
    double SubPageAvailableWidth { get; }

    void RefreshBooks();
    void RefreshCurrentSubPage();
    void SetBottomDock(Microsoft.UI.Xaml.Controls.Border? dock);
    void ShowToast(
        string text,
        bool canUndo = false,
        bool isDelete = false,
        Action? undo = null,
        string? detail = null,
        string? actionLabel = null,
        string? iconKind = null);
    Microsoft.UI.Xaml.Controls.Border CreateCoverView(BookSummary book, double width, double height, bool elevated);
    Microsoft.UI.Xaml.Controls.Primitives.FlyoutBase BuildBookMenuFlyout(BookSummary book);
    void TryOpenBook(BookSummary book, bool autoListen = false, uint? section = null, int? sentence = null, string? askQuestion = null, string? searchQuery = null);
    void OpenPickCollectionForBook(BookSummary book);
    void OpenDeleteForBook(BookSummary book);
    void OpenPodcastInline(string issueId, double? seekSec = null, bool autoPlay = false);
    Task OpenStudioSheetForBookAsync(string bookId, StudioKind kind = StudioKind.BookGuide);
    Task PromptAddRecordBookAsync(ReadingStatus defaultStatus);
    void SyncShelfSortFromPrefs();
}
