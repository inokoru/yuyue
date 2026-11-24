using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Media.Core;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using WinRT.Interop;
using Yuyue.Core;
using Yuyue.Design;

namespace Yuyue.Audiobook;

public sealed record PendingAudiobookImport(
    List<AudioFile> Files,
    List<string> DisplayNames,
    string Title,
    string[] Authors,
    AudioMark[] Marks,
    byte[]? Cover,
    BookSummary? MatchedEbook);

/// <summary>
/// 1:1 implementation of macOS <c>AudioImporter.swift</c> and <c>AudiobookImportSheet.swift</c>:
/// supports <c>.m4b</c>, multi-file audio tracks, folders, <c>.zip</c> archives (<see cref="YuyueCore.AudiobookUnzip"/>),
/// DRM detection (<see cref="YuyueCore.AudiobookIsLocked"/>), editable cover/title/author, track reordering,
/// and automatic shelf ebook pairing (<see cref="YuyueCore.AudiobookPair"/>).
/// </summary>
public static class AudiobookImporter
{
    public static readonly string[] Extensions = [".m4b", ".m4a", ".mp3", ".flac", ".ogg", ".opus", ".aac", ".wav"];
    public static readonly string[] DrmExtensions = [".aa", ".aax", ".m4p"];

    /// <summary>
    /// Probes an audio file's duration in seconds using Shell Music properties with MediaSource fallback.
    /// Used by <see cref="Yuyue.Core.RemoteLibraryService"/> when streaming remote WebDAV audiobooks.
    /// </summary>
    public static async Task<double> ProbeDurationSecondsAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return 0;
        double seconds = 0;
        try
        {
            var sf = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
            var music = await sf.Properties.GetMusicPropertiesAsync();
            seconds = music.Duration.TotalSeconds;
            if (seconds <= 0.1)
            {
                using var ms = MediaSource.CreateFromStorageFile(sf);
                await ms.OpenAsync();
                if (ms.Duration is { } d) seconds = d.TotalSeconds;
            }
        }
        catch { }
        return seconds;
    }

    public static bool IsAudiobookOrArchivePath(string path)
    {
        if (Directory.Exists(path))
        {
            try
            {
                return Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories)
                    .Any(f => Extensions.Contains(Path.GetExtension(f).ToLowerInvariant()));
            }
            catch { return false; }
        }
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return Extensions.Contains(ext) || DrmExtensions.Contains(ext) || ext == ".zip";
    }

    public static string[] ExpandAndValidatePaths(LibraryService library, IReadOnlyList<string> rawPaths)
    {
        var collected = new List<string>();
        var createdUnzipDirs = new List<string>();

        try
        {
            foreach (var raw in rawPaths)
            {
                if (Directory.Exists(raw))
                {
                    collected.AddRange(Directory.EnumerateFiles(raw, "*.*", SearchOption.AllDirectories));
                    continue;
                }

                if (!File.Exists(raw)) continue;
                string ext = Path.GetExtension(raw).ToLowerInvariant();
                if (ext == ".zip")
                {
                    string unzipDir = Path.Combine(library.DataDir, "audiobook-unzip", Guid.NewGuid().ToString("D"));
                    Directory.CreateDirectory(unzipDir);
                    createdUnzipDirs.Add(unzipDir);
                    string[] extracted = library.Core.AudiobookUnzip(raw, unzipDir);
                    collected.AddRange(extracted);
                }
                else
                {
                    collected.Add(raw);
                }
            }

            foreach (var p in collected)
            {
                string ext = Path.GetExtension(p).ToLowerInvariant();
                bool locked = DrmExtensions.Contains(ext);
                if (!locked && Extensions.Contains(ext))
                {
                    try { locked = library.Core.AudiobookIsLocked(p); } catch { }
                }
                if (locked)
                {
                    throw new InvalidOperationException("这份有声书带有 DRM 数字版权保护（如 Audible .aax 或受保护的 .m4b/.m4p），语阅无法直接播放。请导入无加密保护的音频文件。");
                }
            }

            var valid = collected
                .Where(p => File.Exists(p) && Extensions.Contains(Path.GetExtension(p).ToLowerInvariant()))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (valid.Length == 0)
                throw new InvalidOperationException("未在所选路径或压缩包中找到受支持的音频文件（支持 .m4b / .mp3 / .m4a / .flac / .ogg / .opus / .wav）。");

            return library.Core.SortAudioFiles(valid);
        }
        catch
        {
            foreach (string dir in createdUnzipDirs)
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
            }
            throw;
        }
    }

    private static AudioMark[] BuildCombinedChapterMarks(LibraryService library, IReadOnlyList<AudioFile> files)
    {
        if (files.Count == 0) return [];
        var combined = new List<AudioMark>();
        double offset = 0.0;
        foreach (var file in files)
        {
            AudioMark[] own = [];
            try { own = library.Core.AudioChapterMarks(file.Path); } catch { }
            foreach (var m in own)
            {
                combined.Add(new AudioMark(m.Title, offset + Math.Max(0.0, m.Start)));
            }
            offset += Math.Max(0.0, file.Seconds);
        }

        // Preserve single-file embedded marks and multi-file combined marks (when >= 2 marks exist across files).
        if (files.Count == 1)
            return combined.Count >= 1 ? combined.ToArray() : [];
        return combined.Count >= 2 ? combined.ToArray() : [];
    }

    private static void CleanupTemporaryUnzipDirs(string dataDir, IEnumerable<AudioFile> files)
    {
        try
        {
            string unzipRoot = Path.GetFullPath(Path.Combine(dataDir, "audiobook-unzip"));
            if (!Directory.Exists(unzipRoot)) return;
            string prefix = unzipRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

            var toDelete = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in files)
            {
                if (string.IsNullOrWhiteSpace(f.Path)) continue;
                string full = Path.GetFullPath(f.Path);
                if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                string rel = full[prefix.Length..];
                int sep = rel.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
                string bucket = sep > 0 ? rel[..sep] : rel;
                if (!string.IsNullOrWhiteSpace(bucket))
                    toDelete.Add(Path.Combine(unzipRoot, bucket));
            }

            foreach (string dir in toDelete)
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
            }
        }
        catch { }
    }

    public static async Task<PendingAudiobookImport> PrepareImportAsync(LibraryService library, IReadOnlyList<string> rawPaths)
    {
        string[] ordered = await Task.Run(() => ExpandAndValidatePaths(library, rawPaths));

        string title = "";
        string author = "";
        byte[]? cover = null;
        var audioFiles = new List<AudioFile>(ordered.Length);
        var names = new List<string>(ordered.Length);

        // Also look for cover.jpg / folder.jpg in the same folder
        string? parentDir = Path.GetDirectoryName(ordered[0]);
        if (!string.IsNullOrEmpty(parentDir) && Directory.Exists(parentDir))
        {
            foreach (var imgName in new[] { "cover.jpg", "cover.png", "folder.jpg", "folder.png" })
            {
                string cand = Path.Combine(parentDir, imgName);
                if (File.Exists(cand))
                {
                    try { cover = await File.ReadAllBytesAsync(cand); break; } catch { }
                }
            }
        }

        for (int i = 0; i < ordered.Length; i++)
        {
            string path = ordered[i];
            names.Add(Path.GetFileNameWithoutExtension(path));
            double seconds = 0;
            try
            {
                var sf = await StorageFile.GetFileFromPathAsync(path);
                var music = await sf.Properties.GetMusicPropertiesAsync();
                seconds = music.Duration.TotalSeconds;

                if (i == 0)
                {
                    title = !string.IsNullOrWhiteSpace(music.Album)
                        ? music.Album.Trim()
                        : (!string.IsNullOrWhiteSpace(music.Title) ? music.Title.Trim() : Path.GetFileNameWithoutExtension(path));
                    author = !string.IsNullOrWhiteSpace(music.AlbumArtist)
                        ? music.AlbumArtist.Trim()
                        : music.Artist?.Trim() ?? "";

                    if (cover is null)
                    {
                        using var thumb = await sf.GetThumbnailAsync(ThumbnailMode.MusicView, 600, ThumbnailOptions.UseCurrentScale);
                        if (thumb is not null && thumb.Type == ThumbnailType.Image && thumb.Size > 0)
                        {
                            var buf = new byte[thumb.Size];
                            await thumb.ReadAsync(buf.AsBuffer(), (uint)buf.Length, InputStreamOptions.None);
                            cover = buf;
                        }
                    }
                }

                if (seconds <= 0.1)
                {
                    using var ms = MediaSource.CreateFromStorageFile(sf);
                    await ms.OpenAsync();
                    if (ms.Duration is { } d) seconds = d.TotalSeconds;
                }
            }
            catch
            {
                if (i == 0 && string.IsNullOrEmpty(title))
                    title = Path.GetFileNameWithoutExtension(path);
            }

            audioFiles.Add(new AudioFile(path, Math.Max(1.0, seconds)));
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            string? folderName = !string.IsNullOrEmpty(parentDir) ? Path.GetFileName(parentDir) : null;
            title = ordered.Length > 1 && !string.IsNullOrWhiteSpace(folderName)
                ? folderName
                : Path.GetFileNameWithoutExtension(ordered[0]);
        }

        AudioMark[] marks = BuildCombinedChapterMarks(library, audioFiles);

        string[] authors = string.IsNullOrWhiteSpace(author) ? [] : [author];
        var matchedEbook = FindMatchingShelfEbook(library, title);

        return new PendingAudiobookImport(audioFiles, names, title, authors, marks, cover, matchedEbook);
    }

    public static BookSummary? FindMatchingShelfEbook(LibraryService library, string title)
    {
        string norm = NormalizeBookTitle(title);
        if (string.IsNullOrEmpty(norm)) return null;
        return library.GetShelfBooks()
            .FirstOrDefault(b => b.Format != BookFormat.Audio &&
                (NormalizeBookTitle(b.Title).Equals(norm, StringComparison.OrdinalIgnoreCase) ||
                 NormalizeBookTitle(b.Title).Contains(norm, StringComparison.OrdinalIgnoreCase) ||
                 norm.Contains(NormalizeBookTitle(b.Title), StringComparison.OrdinalIgnoreCase)));
    }

    private static string NormalizeBookTitle(string raw) =>
        raw.Trim().Trim('《', '》', '"', '“', '”', ' ', '（', '）', '(', ')');

    public static async Task<BookSummary> ImportFilesAsync(LibraryService library, IReadOnlyList<string> rawPaths)
    {
        var pending = await PrepareImportAsync(library, rawPaths);
        try
        {
            var import = new AudioImport(pending.Files.ToArray(), pending.Title, pending.Authors, pending.Marks, pending.Cover);
            var summary = await Task.Run(() => library.Core.ImportAudiobook(import));
            if (pending.MatchedEbook is not null)
            {
                try { library.Core.AudiobookPair(summary.Id, pending.MatchedEbook.Id); } catch { }
            }
            return summary;
        }
        finally
        {
            CleanupTemporaryUnzipDirs(library.DataDir, pending.Files);
        }
    }

    /// <summary>
    /// 1:1 <c>AudiobookImportSheet.swift</c>: interactive 420px preview sheet before finishing audiobook import.
    /// </summary>
    public static async Task<BookSummary?> ShowImportSheetAsync(
        Window ownerWindow,
        XamlRoot xamlRoot,
        LibraryService library,
        IReadOnlyList<string> rawPaths)
    {
        var pending = await PrepareImportAsync(library, rawPaths);
        bool dark = xamlRoot.Content is FrameworkElement fe && ThemeHelper.IsDark(fe.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(library.Prefs.Tint, dark);

        byte[]? coverBytes = pending.Cover;
        bool pairWithEbook = pending.MatchedEbook is not null;
        bool hasEmbeddedMarks = pending.Marks.Length > 0;

        var root = new StackPanel { Spacing = 12, Width = 420 };

        // Header: 64x64 cover + editable Title / Author / Length + 换封面
        var headGrid = new Grid { ColumnSpacing = 12 };
        headGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(68) });
        headGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var coverImg = new Image { Stretch = Stretch.UniformToFill };
        var coverFallback = YYIcons.Make("headphones", 22, ThemeHelper.Brush(p.T3));
        var coverBox = new Border
        {
            Width = 68,
            Height = 68,
            CornerRadius = new CornerRadius(9),
            Background = ThemeHelper.Brush(p.Inset),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0.75),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new Grid { Children = { coverFallback, coverImg } },
        };

        async Task RefreshCoverPreviewAsync()
        {
            if (coverBytes is { Length: > 0 })
            {
                try
                {
                    using var ms = new InMemoryRandomAccessStream();
                    await ms.WriteAsync(coverBytes.AsBuffer());
                    ms.Seek(0);
                    var bmp = new BitmapImage();
                    await bmp.SetSourceAsync(ms);
                    coverImg.Source = bmp;
                    coverFallback.Visibility = Visibility.Collapsed;
                    return;
                }
                catch { }
            }
            coverImg.Source = null;
            coverFallback.Visibility = Visibility.Visible;
        }
        await RefreshCoverPreviewAsync();
        headGrid.Children.Add(coverBox);

        var titleBox = new TextBox { Text = pending.Title, PlaceholderText = "书名", FontWeight = FontWeights.SemiBold, FontSize = 13 };
        var authorBox = new TextBox { Text = string.Join("、", pending.Authors), PlaceholderText = "作者 / 演播（可选）", FontSize = 12 };
        double totalSec = pending.Files.Sum(f => f.Seconds);
        var metaRow = new Grid();
        metaRow.Children.Add(new TextBlock
        {
            Text = $"{pending.Files.Count} 个音频文件 · {FormatTotalLength(totalSec)}",
            FontSize = 11,
            Foreground = ThemeHelper.Brush(p.T3),
            VerticalAlignment = VerticalAlignment.Center,
        });
        var changeCoverBtn = new Button
        {
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 2, 4, 2),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeHelper.Brush(tint.Accent),
            HorizontalAlignment = HorizontalAlignment.Right,
            Content = "换封面…",
        };
        changeCoverBtn.Click += async (_, _) =>
        {
            var picker = new FileOpenPicker { ViewMode = PickerViewMode.Thumbnail };
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".webp");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(ownerWindow));
            var f = await picker.PickSingleFileAsync();
            if (f is null) return;
            try
            {
                coverBytes = await File.ReadAllBytesAsync(f.Path);
                await RefreshCoverPreviewAsync();
            }
            catch { }
        };
        metaRow.Children.Add(changeCoverBtn);

        var metaStack = new StackPanel { Spacing = 6, Children = { titleBox, authorBox, metaRow } };
        Grid.SetColumn(metaStack, 1);
        headGrid.Children.Add(metaStack);
        root.Children.Add(headGrid);

        // Chapter / Track order list
        root.Children.Add(new TextBlock
        {
            Text = hasEmbeddedMarks ? "章节 · 文件里自带的" : "章节顺序 · 按文件名里的数字排",
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeHelper.Brush(p.T2),
        });

        var tracksPanel = new StackPanel { Spacing = 0 };
        void RebuildTrackList()
        {
            tracksPanel.Children.Clear();
            if (hasEmbeddedMarks)
            {
                for (int i = 0; i < pending.Marks.Length; i++)
                {
                    if (i > 0) tracksPanel.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(10, 0, 10, 0) });
                    var m = pending.Marks[i];
                    var row = new Grid { Padding = new Thickness(10, 7, 10, 7), ColumnSpacing = 8 };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.Children.Add(new TextBlock { Text = $"{i + 1}", FontFamily = new FontFamily(YYFontFamily.Mono.WindowsFamily()), FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3) });
                    var nameTb = new TextBlock { Text = string.IsNullOrWhiteSpace(m.Title) ? $"第 {i + 1} 集" : m.Title, FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T1), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis };
                    Grid.SetColumn(nameTb, 1);
                    row.Children.Add(nameTb);
                    tracksPanel.Children.Add(row);
                }
                return;
            }

            for (int i = 0; i < pending.Files.Count; i++)
            {
                if (i > 0) tracksPanel.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(10, 0, 10, 0) });
                int idx = i;
                var row = new Grid { Padding = new Thickness(10, 6, 8, 6), ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                row.Children.Add(new TextBlock { Text = $"{i + 1}", FontFamily = new FontFamily(YYFontFamily.Mono.WindowsFamily()), FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Center });
                var nameTb = new TextBlock
                {
                    Text = pending.DisplayNames[i],
                    FontFamily = new FontFamily(YYFontFamily.Mono.WindowsFamily()),
                    FontSize = 11,
                    Foreground = ThemeHelper.Brush(p.T2),
                    VerticalAlignment = VerticalAlignment.Center,
                    MaxLines = 1,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                Grid.SetColumn(nameTb, 1);
                row.Children.Add(nameTb);

                var durTb = new TextBlock
                {
                    Text = FormatClock(pending.Files[i].Seconds),
                    FontFamily = new FontFamily(YYFontFamily.Mono.WindowsFamily()),
                    FontSize = 10.5,
                    Foreground = ThemeHelper.Brush(p.T3),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(durTb, 2);
                row.Children.Add(durTb);

                if (pending.Files.Count > 1)
                {
                    var moveStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
                    var upBtn = new Button { Style = (Style)Application.Current.Resources["YYIconButtonStyle"], Content = "↑", Width = 22, Height = 22, FontSize = 10.5, IsEnabled = idx > 0 };
                    upBtn.Click += (_, _) =>
                    {
                        (pending.Files[idx - 1], pending.Files[idx]) = (pending.Files[idx], pending.Files[idx - 1]);
                        (pending.DisplayNames[idx - 1], pending.DisplayNames[idx]) = (pending.DisplayNames[idx], pending.DisplayNames[idx - 1]);
                        RebuildTrackList();
                    };
                    var downBtn = new Button { Style = (Style)Application.Current.Resources["YYIconButtonStyle"], Content = "↓", Width = 22, Height = 22, FontSize = 10.5, IsEnabled = idx < pending.Files.Count - 1 };
                    downBtn.Click += (_, _) =>
                    {
                        (pending.Files[idx + 1], pending.Files[idx]) = (pending.Files[idx], pending.Files[idx + 1]);
                        (pending.DisplayNames[idx + 1], pending.DisplayNames[idx]) = (pending.DisplayNames[idx], pending.DisplayNames[idx + 1]);
                        RebuildTrackList();
                    };
                    moveStack.Children.Add(upBtn);
                    moveStack.Children.Add(downBtn);
                    Grid.SetColumn(moveStack, 3);
                    row.Children.Add(moveStack);
                }

                tracksPanel.Children.Add(row);
            }
        }
        RebuildTrackList();

        root.Children.Add(new ScrollViewer
        {
            MaxHeight = 220,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new Border
            {
                CornerRadius = new CornerRadius(12),
                Background = ThemeHelper.Brush(p.Card),
                BorderBrush = ThemeHelper.Brush(p.Line),
                BorderThickness = new Thickness(1),
                Child = tracksPanel,
            },
        });

        if (!hasEmbeddedMarks && pending.Files.Count > 1)
        {
            root.Children.Add(new TextBlock
            {
                Text = "“第 2 集”已自动排在“第 10 集”前面；若顺序不对可点右侧 ↑↓ 调整。",
                FontSize = 10.5,
                Foreground = ThemeHelper.Brush(p.T3),
            });
        }

        // Auto-pair switch when matching shelf ebook exists (1:1 AudiobookImportSheet.swift:67-79)
        if (pending.MatchedEbook is BookSummary match)
        {
            var pairGrid = new Grid { ColumnSpacing = 10 };
            pairGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            pairGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var pairText = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            pairText.Children.Add(new TextBlock { Text = $"书架上有《{match.Title}》电子书", FontSize = 12, FontWeight = FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1) });
            pairText.Children.Add(new TextBlock { Text = "导入后配对合成听读书：切换阅读时自动同步进度", FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3) });
            pairGrid.Children.Add(pairText);
            var pairSw = new ToggleSwitch { IsOn = true, MinWidth = 0, OnContent = "", OffContent = "", VerticalAlignment = VerticalAlignment.Center };
            pairSw.Toggled += (_, _) => pairWithEbook = pairSw.IsOn;
            Grid.SetColumn(pairSw, 1);
            pairGrid.Children.Add(pairSw);

            root.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(12),
                Background = ThemeHelper.Brush(p.Card),
                BorderBrush = ThemeHelper.Brush(p.Line),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 9, 12, 9),
                Child = pairGrid,
            });
        }

        var dlg = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "导入有声书",
            Content = root,
            PrimaryButtonText = "完成",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        try
        {
            if (await dlg.ShowAsync() != ContentDialogResult.Primary) return null;

            string finalTitle = string.IsNullOrWhiteSpace(titleBox.Text) ? pending.Title : titleBox.Text.Trim();
            string[] finalAuthors = authorBox.Text
                .Split(['、', ',', '，', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var finalMarks = pending.Files.Count > 1
                ? BuildCombinedChapterMarks(library, pending.Files)
                : pending.Marks;

            var finalImport = new AudioImport(pending.Files.ToArray(), finalTitle, finalAuthors, finalMarks, coverBytes);
            var imported = await Task.Run(() => library.Core.ImportAudiobook(finalImport));
            if (pairWithEbook && pending.MatchedEbook is not null)
            {
                try { library.Core.AudiobookPair(imported.Id, pending.MatchedEbook.Id); } catch { }
            }
            return imported;
        }
        finally
        {
            CleanupTemporaryUnzipDirs(library.DataDir, pending.Files);
        }
    }

    private static string FormatTotalLength(double sec)
    {
        int totalMin = Math.Max(1, (int)Math.Round(sec / 60.0));
        return totalMin >= 60 ? $"约 {totalMin / 60} 小时 {totalMin % 60} 分" : $"约 {totalMin} 分钟";
    }

    private static string FormatClock(double sec)
    {
        var ts = System.TimeSpan.FromSeconds(Math.Max(0, sec));
        return ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}" : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }
}
