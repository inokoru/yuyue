using System.Text.Json;
using Yuyue.Audiobook;

namespace Yuyue.Core;

/// <summary>
/// Recursively watches a user-configured local directory and automatically imports newly added
/// or updated books and audiobooks, persisting seen file fingerprints across restarts.
/// </summary>
public sealed class WatchedFolderService : IDisposable
{
    private readonly LibraryService _library;
    private readonly string _seenStorePath;
    private FileSystemWatcher? _watcher;
    private HashSet<string> _seenFingerprints = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public event Action? Imported;

    public WatchedFolderService(LibraryService library)
    {
        _library = library;
        _seenStorePath = Path.Combine(library.DataDir, "watched-seen.json");
        _seenFingerprints = LoadFingerprints(_seenStorePath);
        Reload();
    }

    internal static string ComputeFingerprint(string fullPath, long size, long mtimeUtcTicks) =>
        $"{Path.GetFullPath(fullPath).ToLowerInvariant()}|{size}|{mtimeUtcTicks}";

    internal static HashSet<string> LoadFingerprints(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path));
                if (list is not null)
                    return new HashSet<string>(list, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch { }
        return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    internal static void SaveFingerprints(string path, HashSet<string> fingerprints)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            // Bound to latest 4000 entries to prevent unbounded growth
            IEnumerable<string> toSave = fingerprints.Count > 4000
                ? fingerprints.Skip(fingerprints.Count - 4000)
                : fingerprints;
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(toSave));
            File.Move(tmp, path, overwrite: true);
        }
        catch { }
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
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            _watcher.Created += (_, e) => _ = QueueCandidateAsync(e.FullPath);
            _watcher.Renamed += (_, e) => _ = QueueCandidateAsync(e.FullPath);
            _watcher.Changed += (_, e) => _ = QueueCandidateAsync(e.FullPath);

            // Recursive initial scan for unseen or updated files in watched folder
            _ = Task.Run(async () =>
            {
                try
                {
                    var opts = new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        IgnoreInaccessible = true,
                        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
                    };
                    foreach (var f in Directory.EnumerateFiles(folder, "*", opts))
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

        string fp;
        try
        {
            var fi = new FileInfo(fullPath);
            if (fi.Length == 0) return;
            fp = ComputeFingerprint(fullPath, fi.Length, fi.LastWriteTimeUtc.Ticks);
        }
        catch { return; }

        await _gate.WaitAsync();
        try
        {
            if (!_seenFingerprints.Add(fp)) return;

            // Ensure file isn't still locked by browser/downloader
            await using (var probe = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read)) { }

            BookSummary imported = isAudio
                ? await AudiobookImporter.ImportFilesAsync(_library, [fullPath])
                : await _library.ImportBookWithExtrasAsync(fullPath);
            _library.Analytics.TrackBookImported(imported.Format.ToString(), "other", fullPath);

            SaveFingerprints(_seenStorePath, _seenFingerprints);
            Imported?.Invoke();
        }
        catch
        {
            _seenFingerprints.Remove(fp);
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
