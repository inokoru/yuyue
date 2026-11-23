using Yuyue.Audiobook;

namespace Yuyue.Core;

/// <summary>
/// Watches a user-configured local directory and automatically imports newly added books or audiobooks.
/// </summary>
public sealed class WatchedFolderService : IDisposable
{
    private readonly LibraryService _library;
    private FileSystemWatcher? _watcher;
    private readonly HashSet<string> _seenPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public event Action? Imported;

    public WatchedFolderService(LibraryService library)
    {
        _library = library;
        Reload();
    }

    public void Reload()
    {
        Stop();
        string? folder = _library.Prefs.WatchedFolder;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;

        try
        {
            _watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            _watcher.Created += (_, e) => _ = QueueCandidateAsync(e.FullPath);
            _watcher.Renamed += (_, e) => _ = QueueCandidateAsync(e.FullPath);

            // Initial scan for existing files in watched folder
            _ = Task.Run(async () =>
            {
                try
                {
                    foreach (var f in Directory.GetFiles(folder))
                        await QueueCandidateAsync(f, delayMs: 50);
                }
                catch { }
            });
        }
        catch { }
    }

    private async Task QueueCandidateAsync(string fullPath, int delayMs = 1200)
    {
        string ext = Path.GetExtension(fullPath).ToLowerInvariant();
        bool isBook = LibraryService.SupportedExtensions.Contains(ext);
        bool isAudio = AudiobookImporter.Extensions.Contains(ext);
        if (!isBook && !isAudio) return;

        await Task.Delay(delayMs);
        if (!File.Exists(fullPath)) return;

        await _gate.WaitAsync();
        try
        {
            if (!_seenPaths.Add(fullPath)) return;

            // Ensure file isn't still locked by browser/downloader
            await using (var probe = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read)) { }

            if (isAudio)
                await AudiobookImporter.ImportFilesAsync(_library, [fullPath]);
            else
                await _library.ImportBookWithExtrasAsync(fullPath);

            Imported?.Invoke();
        }
        catch
        {
            _seenPaths.Remove(fullPath);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Stop()
    {
        if (_watcher is not null)
        {
            try { _watcher.EnableRaisingEvents = false; _watcher.Dispose(); } catch { }
            _watcher = null;
        }
    }

    public void Dispose() => Stop();
}
