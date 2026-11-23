using System.Text.Json;

namespace Yuyue.Settings;

public sealed class SettingsHistoryRecord
{
    public DateTimeOffset Time { get; set; } = DateTimeOffset.Now;
    public string Page { get; set; } = "general";
    public string Category { get; set; } = "通用";
    public string Title { get; set; } = "";
    public string OldValue { get; set; } = "";
    public string NewValue { get; set; } = "";

    [System.Text.Json.Serialization.JsonIgnore]
    public Action? Undo { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public Action? Redo { get; set; }
}

/// <summary>
/// Persists up to 120 settings change entries to <c>settings-history.json</c>, folds rapid adjacent edits
/// of the same setting within 10 seconds, and maintains an in-session Redo stack.
/// </summary>
public sealed class SettingsHistoryStore
{
    public const int MaxEntries = 120;
    private static readonly System.TimeSpan FoldWindow = System.TimeSpan.FromSeconds(10);

    private readonly string _filePath;
    private readonly List<SettingsHistoryRecord> _entries = [];
    private readonly Stack<SettingsHistoryRecord> _redoStack = new();

    public IReadOnlyList<SettingsHistoryRecord> Entries => _entries;
    public bool CanRedo => _redoStack.Count > 0;
    public SettingsHistoryRecord? NextRedo => _redoStack.Count > 0 ? _redoStack.Peek() : null;

    public SettingsHistoryStore(string dataDir)
    {
        _filePath = Path.Combine(dataDir, "settings-history.json");
        Load();
    }

    public static string CategoryOfPage(string page) => page switch
    {
        "appearance" => "外观",
        "reading" or "foreign" => "阅读",
        "listening" => "朗读",
        "companion" => "书伴",
        "sync" or "export" or "storage" or "data" => "同步",
        _ => "通用",
    };

    public void Record(
        string page,
        string title,
        string newValue,
        string? oldValue = null,
        Action? undo = null,
        Action? redo = null,
        DateTimeOffset? now = null)
    {
        var ts = now ?? DateTimeOffset.Now;
        _redoStack.Clear();

        if (_entries.Count > 0)
        {
            var top = _entries[0];
            if (string.Equals(top.Page, page, StringComparison.Ordinal) &&
                string.Equals(top.Title, title, StringComparison.Ordinal) &&
                (ts - top.Time) <= FoldWindow)
            {
                top.Time = ts;
                top.NewValue = newValue;
                if (redo is not null) top.Redo = redo;
                Save();
                return;
            }
        }

        _entries.Insert(0, new SettingsHistoryRecord
        {
            Time = ts,
            Page = page,
            Category = CategoryOfPage(page),
            Title = title,
            OldValue = oldValue ?? "",
            NewValue = newValue,
            Undo = undo,
            Redo = redo,
        });

        while (_entries.Count > MaxEntries)
            _entries.RemoveAt(_entries.Count - 1);

        Save();
    }

    public SettingsHistoryRecord? TryUndo(SettingsHistoryRecord? specific = null)
    {
        var target = specific ?? _entries.FirstOrDefault(e => e.Undo is not null);
        if (target?.Undo is null) return null;
        _entries.Remove(target);
        target.Undo();
        if (target.Redo is not null) _redoStack.Push(target);
        Save();
        return target;
    }

    public SettingsHistoryRecord? TryRedo()
    {
        if (_redoStack.Count == 0) return null;
        var target = _redoStack.Pop();
        target.Redo?.Invoke();
        target.Time = DateTimeOffset.Now;
        _entries.Insert(0, target);
        while (_entries.Count > MaxEntries)
            _entries.RemoveAt(_entries.Count - 1);
        Save();
        return target;
    }

    public void Clear()
    {
        _entries.Clear();
        _redoStack.Clear();
        Save();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var loaded = JsonSerializer.Deserialize<List<SettingsHistoryRecord>>(File.ReadAllText(_filePath));
            if (loaded is not null)
            {
                _entries.Clear();
                _entries.AddRange(loaded.Take(MaxEntries));
            }
        }
        catch { }
    }

    private void Save()
    {
        try
        {
            string? dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = _filePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_entries));
            File.Move(tmp, _filePath, overwrite: true);
        }
        catch { }
    }
}
