using System.Text.Json;

namespace Yuyue.Settings;

public sealed class SettingsHistoryRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset Time { get; set; } = DateTimeOffset.Now;
    public string Page { get; set; } = "general";
    public string Category { get; set; } = "通用";
    public string Title { get; set; } = "";
    public string OldValue { get; set; } = "";
    public string NewValue { get; set; } = "";
    public bool Undone { get; set; } = false;

    // Optional serializable target so Undo/Redo works even after restarting the app
    public string? PrefKey { get; set; }
    public string? OldRawJson { get; set; }
    public string? NewRawJson { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public Action? Undo { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public Action? Redo { get; set; }
}

/// <summary>
/// Persists up to 120 settings change entries to <c>settings-history.json</c>, folds rapid adjacent edits
/// of the same setting within 10 seconds, supports both stack-based Undo/Redo and in-place macOS row toggle
/// (<c>MacSettingsHistory.swift</c>), and rehydrates persistent property changes across sessions.
/// </summary>
public sealed class SettingsHistoryStore
{
    public const int MaxEntries = 120;
    private static readonly System.TimeSpan FoldWindow = System.TimeSpan.FromSeconds(10);

    private readonly string _filePath;
    private readonly List<SettingsHistoryRecord> _entries = [];
    private readonly Stack<SettingsHistoryRecord> _redoStack = new();

    /// <summary>
    /// Optional persistent actuator invoked when in-memory <c>Undo</c>/<c>Redo</c> delegates are null after reload.
    /// Parameters: <c>(record, applyUndo)</c> → <c>true</c> if applied.
    /// </summary>
    public Func<SettingsHistoryRecord, bool, bool>? PersistentActuator { get; set; }

    public IReadOnlyList<SettingsHistoryRecord> Entries => _entries;
    public bool CanRedo => _redoStack.Count > 0 || _entries.Any(e => e.Undone && CanActuate(e, applyUndo: false));
    public SettingsHistoryRecord? NextRedo => _redoStack.Count > 0 ? _redoStack.Peek() : _entries.FirstOrDefault(e => e.Undone);

    public int TodayCount
    {
        get
        {
            var today = DateTimeOffset.Now.Date;
            return _entries.Count(e => !e.Undone && e.Time.ToLocalTime().Date == today);
        }
    }

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

    public bool CanActuate(SettingsHistoryRecord rec, bool applyUndo)
    {
        if (applyUndo)
            return rec.Undo is not null || (!string.IsNullOrEmpty(rec.PrefKey) && rec.OldRawJson is not null && PersistentActuator is not null);
        return rec.Redo is not null || (!string.IsNullOrEmpty(rec.PrefKey) && rec.NewRawJson is not null && PersistentActuator is not null);
    }

    private bool InvokeActuate(SettingsHistoryRecord rec, bool applyUndo)
    {
        if (applyUndo)
        {
            if (rec.Undo is not null) { rec.Undo(); return true; }
            if (PersistentActuator is not null && !string.IsNullOrEmpty(rec.PrefKey) && rec.OldRawJson is not null)
                return PersistentActuator(rec, true);
            return false;
        }
        else
        {
            if (rec.Redo is not null) { rec.Redo(); return true; }
            if (PersistentActuator is not null && !string.IsNullOrEmpty(rec.PrefKey) && rec.NewRawJson is not null)
                return PersistentActuator(rec, false);
            return false;
        }
    }

    public void Record(
        string page,
        string title,
        string newValue,
        string? oldValue = null,
        Action? undo = null,
        Action? redo = null,
        DateTimeOffset? now = null,
        string? prefKey = null,
        string? oldRawJson = null,
        string? newRawJson = null)
    {
        var ts = now ?? DateTimeOffset.Now;
        _redoStack.Clear();

        if (_entries.Count > 0)
        {
            var top = _entries[0];
            if (!top.Undone &&
                string.Equals(top.Page, page, StringComparison.Ordinal) &&
                string.Equals(top.Title, title, StringComparison.Ordinal) &&
                (ts - top.Time) <= FoldWindow)
            {
                top.Time = ts;
                top.NewValue = newValue;
                if (redo is not null) top.Redo = redo;
                if (newRawJson is not null) top.NewRawJson = newRawJson;
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
            PrefKey = prefKey,
            OldRawJson = oldRawJson,
            NewRawJson = newRawJson,
        });

        while (_entries.Count > MaxEntries)
            _entries.RemoveAt(_entries.Count - 1);

        Save();
    }

    /// <summary>
    /// Toggles an entry in-place between active and undone (strikethrough + 还原), matching <c>MacSettingsHistory.swift</c>.
    /// </summary>
    public bool ToggleInPlace(SettingsHistoryRecord target)
    {
        bool wantUndo = !target.Undone;
        if (!CanActuate(target, wantUndo)) return false;
        if (!InvokeActuate(target, wantUndo)) return false;
        target.Undone = wantUndo;
        Save();
        return true;
    }

    public SettingsHistoryRecord? TryUndo(SettingsHistoryRecord? specific = null)
    {
        var target = specific ?? _entries.FirstOrDefault(e => !e.Undone && CanActuate(e, applyUndo: true));
        if (target is null || !CanActuate(target, applyUndo: true)) return null;
        _entries.Remove(target);
        InvokeActuate(target, applyUndo: true);
        if (CanActuate(target, applyUndo: false)) _redoStack.Push(target);
        Save();
        return target;
    }

    public SettingsHistoryRecord? TryRedo()
    {
        if (_redoStack.Count == 0)
        {
            var inPlace = _entries.FirstOrDefault(e => e.Undone && CanActuate(e, applyUndo: false));
            if (inPlace is not null && ToggleInPlace(inPlace))
                return inPlace;
            return null;
        }
        var target = _redoStack.Pop();
        InvokeActuate(target, applyUndo: false);
        target.Undone = false;
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
