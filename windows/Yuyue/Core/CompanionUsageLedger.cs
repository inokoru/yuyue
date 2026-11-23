using System.Text.Json;

namespace Yuyue.Core;

public enum UsageSource
{
    Drama,
    Companion,
    Polish,
    Recap,
    Podcast,
    Other,
}

public sealed class UsageDayRecord
{
    public string Date { get; set; } = "";
    public long CachedInput { get; set; }
    public long MissInput { get; set; }
    public long Output { get; set; }
    public int Requests { get; set; }
    public Dictionary<string, long> Sources { get; set; } = new();
    public long TtsChars { get; set; }
    public long TtsSavedChars { get; set; }

    public long TotalTokens => CachedInput + MissInput + Output;
    public long TotalInput => CachedInput + MissInput;
    public double? CacheHitRate => TotalInput > 0 ? (double)CachedInput / TotalInput : null;

    public string ShortLabel
    {
        get
        {
            var parts = Date.Split('-');
            if (parts.Length == 3 && int.TryParse(parts[1], out int m) && int.TryParse(parts[2], out int d))
                return $"{m}/{d}";
            return Date;
        }
    }
}

public sealed record UsageSourceShare(UsageSource Source, long Tokens, double Share);

public sealed record UsageSnapshot30Days(
    IReadOnlyList<UsageDayRecord> Days,
    long TotalTokens,
    long CachedInputTokens,
    long MissInputTokens,
    long OutputTokens,
    long PeakTokens,
    int PeakIndex,
    double AverageTokens,
    IReadOnlyList<UsageSourceShare> Sources,
    long TtsChars,
    long TtsSavedChars)
{
    public double? CacheHitRate
    {
        get
        {
            long totalIn = CachedInputTokens + MissInputTokens;
            return totalIn > 0 ? (double)CachedInputTokens / totalIn : null;
        }
    }
}

public sealed class BudgetExceededException : InvalidOperationException
{
    public bool IsTts { get; }

    public BudgetExceededException(string message, bool isTts) : base(message)
    {
        IsTts = isTts;
    }
}

/// <summary>
/// 1:1 port of <c>CompanionUsage</c> in <c>apple/Yuyue/App/Companion.swift</c>:
/// persistent 30-day ledger tracking paid cloud LLM input/output/cached tokens by source
/// and paid cloud TTS rendered vs cache-saved characters, with daily budget cap enforcement.
/// </summary>
public sealed class CompanionUsageLedger
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };
    private readonly object _gate = new();
    private readonly string _ledgerPath;
    private readonly Func<long> _getDailyTokenLimit;
    private readonly Func<long> _getDailyTtsCharLimit;
    private Dictionary<string, UsageDayRecord> _days;

    public event Action? Changed;

    public CompanionUsageLedger(string dataDir, Func<long> getDailyTokenLimit, Func<long> getDailyTtsCharLimit)
    {
        _ledgerPath = Path.Combine(dataDir, "companion-usage.json");
        _getDailyTokenLimit = getDailyTokenLimit;
        _getDailyTtsCharLimit = getDailyTtsCharLimit;
        _days = Load();
    }

    public static string SourceTitle(UsageSource s) => s switch
    {
        UsageSource.Drama => "广播剧认人",
        UsageSource.Companion => "书伴问答",
        UsageSource.Polish => "错字整理",
        UsageSource.Recap => "前情提要",
        UsageSource.Podcast => "播客",
        _ => "其他",
    };

    public static string SourceKey(UsageSource s) => s switch
    {
        UsageSource.Drama => "drama",
        UsageSource.Companion => "companion",
        UsageSource.Polish => "polish",
        UsageSource.Recap => "recap",
        UsageSource.Podcast => "podcast",
        _ => "other",
    };

    private static UsageSource ParseSource(string key) => key.ToLowerInvariant() switch
    {
        "drama" => UsageSource.Drama,
        "companion" => UsageSource.Companion,
        "polish" => UsageSource.Polish,
        "recap" => UsageSource.Recap,
        "podcast" => UsageSource.Podcast,
        _ => UsageSource.Other,
    };

    public void CheckLlmBudget(bool paid)
    {
        if (!paid) return;
        long limit = _getDailyTokenLimit();
        if (limit <= 0) return;

        string today = DateKey(DateTime.Now);
        long used;
        lock (_gate)
        {
            used = _days.TryGetValue(today, out var rec) ? rec.TotalTokens : 0;
        }
        if (used >= limit)
        {
            throw new BudgetExceededException(
                $"已达到今日用量上限（{FormatCount(limit)} token），已自动暂停。可在「设置 · 书伴」中调整。",
                isTts: false);
        }
    }

    public void RecordLlm(long input, long output, long cached, bool paid, UsageSource source = UsageSource.Other)
    {
        if (!paid || (input <= 0 && output <= 0 && cached <= 0)) return;
        string today = DateKey(DateTime.Now);
        string srcKey = SourceKey(source);
        lock (_gate)
        {
            if (!_days.TryGetValue(today, out var rec))
            {
                rec = new UsageDayRecord { Date = today };
                _days[today] = rec;
            }
            long c = Math.Max(0, cached);
            long m = Math.Max(0, input);
            long o = Math.Max(0, output);
            rec.CachedInput += c;
            rec.MissInput += m;
            rec.Output += o;
            rec.Requests += 1;
            rec.Sources[srcKey] = rec.Sources.GetValueOrDefault(srcKey) + c + m + o;
            SaveLocked();
        }
        Changed?.Invoke();
    }

    public void CheckTtsBudget(int chars)
    {
        long limit = _getDailyTtsCharLimit();
        if (limit <= 0) return;

        string today = DateKey(DateTime.Now);
        long used;
        lock (_gate)
        {
            used = _days.TryGetValue(today, out var rec) ? rec.TtsChars : 0;
        }
        if (used + Math.Max(0, chars) > limit)
        {
            throw new BudgetExceededException(
                $"已达到今日云端朗读上限（{FormatCount(limit)}字），已自动暂停。可在「设置 · 书伴」中调整。",
                isTts: true);
        }
    }

    public void RecordTts(int chars, bool cached)
    {
        if (chars <= 0) return;
        string today = DateKey(DateTime.Now);
        lock (_gate)
        {
            if (!_days.TryGetValue(today, out var rec))
            {
                rec = new UsageDayRecord { Date = today };
                _days[today] = rec;
            }
            if (cached) rec.TtsSavedChars += chars;
            else rec.TtsChars += chars;
            SaveLocked();
        }
        Changed?.Invoke();
    }

    public UsageDayRecord GetToday()
    {
        string today = DateKey(DateTime.Now);
        lock (_gate)
        {
            return _days.TryGetValue(today, out var rec)
                ? CloneRecord(rec)
                : new UsageDayRecord { Date = today };
        }
    }

    public UsageSnapshot30Days Snapshot30Days()
    {
        var keys = Last30DateKeys(DateTime.Now);
        var list = new List<UsageDayRecord>(keys.Count);
        lock (_gate)
        {
            foreach (string k in keys)
            {
                list.Add(_days.TryGetValue(k, out var rec)
                    ? CloneRecord(rec)
                    : new UsageDayRecord { Date = k });
            }
        }

        long total = 0, cached = 0, miss = 0, output = 0, tts = 0, ttsSaved = 0;
        long peak = 0;
        int peakIdx = Math.Max(0, list.Count - 1);
        var sourceTotals = new Dictionary<UsageSource, long>();

        for (int i = 0; i < list.Count; i++)
        {
            var day = list[i];
            long t = day.TotalTokens;
            total += t;
            cached += day.CachedInput;
            miss += day.MissInput;
            output += day.Output;
            tts += day.TtsChars;
            ttsSaved += day.TtsSavedChars;
            if (t > peak)
            {
                peak = t;
                peakIdx = i;
            }
            foreach (var kv in day.Sources)
            {
                if (kv.Value <= 0) continue;
                var s = ParseSource(kv.Key);
                sourceTotals[s] = sourceTotals.GetValueOrDefault(s) + kv.Value;
            }
        }

        var breakdown = total > 0
            ? sourceTotals
                .Select(kv => new UsageSourceShare(kv.Key, kv.Value, (double)kv.Value / total))
                .OrderByDescending(x => x.Tokens)
                .ToArray()
            : Array.Empty<UsageSourceShare>();

        return new UsageSnapshot30Days(
            list,
            total,
            cached,
            miss,
            output,
            peak,
            peakIdx,
            (double)total / Math.Max(1, list.Count),
            breakdown,
            tts,
            ttsSaved);
    }

    public static string FormatCount(long n)
    {
        if (n >= 10_000)
        {
            double wan = n / 10_000.0;
            if (wan >= 100) return $"{wan:0} 万";
            string s = wan.ToString("0.0");
            return (s.EndsWith(".0", StringComparison.Ordinal) ? s[..^2] : s) + " 万";
        }
        if (n >= 1_000)
        {
            double k = n / 1_000.0;
            string s = k.ToString("0.0");
            return (s.EndsWith(".0", StringComparison.Ordinal) ? s[..^2] : s) + "K";
        }
        return n.ToString();
    }

    private static string DateKey(DateTime dt) => dt.ToString("yyyy-MM-dd");

    private static List<string> Last30DateKeys(DateTime now)
    {
        var today = now.Date;
        var keys = new List<string>(30);
        for (int offset = -29; offset <= 0; offset++)
            keys.Add(DateKey(today.AddDays(offset)));
        return keys;
    }

    private Dictionary<string, UsageDayRecord> Load()
    {
        try
        {
            if (!File.Exists(_ledgerPath)) return new Dictionary<string, UsageDayRecord>(StringComparer.Ordinal);
            var arr = JsonSerializer.Deserialize<List<UsageDayRecord>>(File.ReadAllText(_ledgerPath));
            if (arr is null) return new Dictionary<string, UsageDayRecord>(StringComparer.Ordinal);
            return arr.Where(r => !string.IsNullOrEmpty(r.Date)).ToDictionary(r => r.Date, r => r, StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, UsageDayRecord>(StringComparer.Ordinal);
        }
    }

    private void SaveLocked()
    {
        try
        {
            var keep = new HashSet<string>(Last30DateKeys(DateTime.Now), StringComparer.Ordinal);
            var pruned = _days.Values
                .Where(r => keep.Contains(r.Date))
                .OrderBy(r => r.Date, StringComparer.Ordinal)
                .ToList();
            _days = pruned.ToDictionary(r => r.Date, r => r, StringComparer.Ordinal);
            string? dir = Path.GetDirectoryName(_ledgerPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = _ledgerPath + ".part";
            File.WriteAllText(tmp, JsonSerializer.Serialize(pruned, JsonOpts));
            File.Move(tmp, _ledgerPath, overwrite: true);
        }
        catch { }
    }

    private static UsageDayRecord CloneRecord(UsageDayRecord r) => new()
    {
        Date = r.Date,
        CachedInput = r.CachedInput,
        MissInput = r.MissInput,
        Output = r.Output,
        Requests = r.Requests,
        Sources = new Dictionary<string, long>(r.Sources),
        TtsChars = r.TtsChars,
        TtsSavedChars = r.TtsSavedChars,
    };
}
