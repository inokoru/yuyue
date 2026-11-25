using System.Globalization;

namespace Yuyue.Core;

public sealed record ReadingHeatmapCell(
    DateOnly Date,
    string DayKey,
    uint ReadSeconds,
    uint ListenSeconds,
    int Level,
    bool IsFuture)
{
    public uint TotalSeconds => ReadSeconds + ListenSeconds;
}

public sealed record ReadingHeatmapSummary(
    IReadOnlyList<IReadOnlyList<ReadingHeatmapCell>> Weeks,
    DateOnly RangeStart,
    DateOnly RangeEnd,
    int ActiveDays,
    uint TotalReadSeconds,
    uint TotalListenSeconds,
    int CurrentStreak,
    int LongestStreak)
{
    public uint TotalSeconds => TotalReadSeconds + TotalListenSeconds;
}

/// <summary>
/// Pure helper computing a Monday-aligned reading heatmap, streak counters, and daily review boundary
/// from <see cref="YuyueCore.DayTimes"/> (parity with Apple's ReadingHeatmap).
/// </summary>
public static class ReadingHeatmapBuilder
{
    public static long LocalDayStartUnixMs(DateTimeOffset now)
    {
        var local = now.ToLocalTime();
        var start = new DateTimeOffset(local.Year, local.Month, local.Day, 0, 0, 0, local.Offset);
        return start.ToUnixTimeMilliseconds();
    }

    public static string FormatDayKey(DateOnly date) =>
        date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// Intensity bucket 0..4 based on total seconds read+listened on a day:
    /// 0 = 0s, 1 = &lt;15m, 2 = 15..30m, 3 = 30..60m, 4 = &gt;=60m.
    /// </summary>
    public static int IntensityLevel(uint totalSeconds) => totalSeconds switch
    {
        0 => 0,
        < 15 * 60 => 1,
        < 30 * 60 => 2,
        < 60 * 60 => 3,
        _ => 4,
    };

    /// <summary>
    /// Builds a Monday-aligned grid of <paramref name="weekCount"/> columns ending with the week containing
    /// <paramref name="today"/>.
    /// </summary>
    public static ReadingHeatmapSummary Build(
        IReadOnlyList<DayTime> dayTimes,
        DateOnly today,
        int weekCount = 26)
    {
        if (weekCount < 1) weekCount = 1;

        var totals = new Dictionary<string, (uint Read, uint Listen)>(StringComparer.Ordinal);
        foreach (var dt in dayTimes)
        {
            if (string.IsNullOrWhiteSpace(dt.Day)) continue;
            totals.TryGetValue(dt.Day, out var acc);
            totals[dt.Day] = (acc.Read + dt.ReadSeconds, acc.Listen + dt.ListenSeconds);
        }

        // Monday = 0, Tuesday = 1, ..., Sunday = 6
        int daysSinceMonday = ((int)today.DayOfWeek + 6) % 7;
        DateOnly thisMonday = today.AddDays(-daysSinceMonday);
        DateOnly startMonday = thisMonday.AddDays(-(weekCount - 1) * 7);
        DateOnly gridEnd = thisMonday.AddDays(6);

        var weeks = new List<IReadOnlyList<ReadingHeatmapCell>>(weekCount);
        int activeDays = 0;
        uint totalRead = 0;
        uint totalListen = 0;
        int longestStreak = 0;
        int runningStreak = 0;

        for (int w = 0; w < weekCount; w++)
        {
            var col = new ReadingHeatmapCell[7];
            for (int d = 0; d < 7; d++)
            {
                DateOnly date = startMonday.AddDays(w * 7 + d);
                string key = FormatDayKey(date);
                bool future = date > today;
                uint r = 0, l = 0;
                if (!future && totals.TryGetValue(key, out var val))
                {
                    r = val.Read;
                    l = val.Listen;
                }
                uint sum = r + l;
                int level = future ? 0 : IntensityLevel(sum);
                col[d] = new ReadingHeatmapCell(date, key, r, l, level, future);

                if (!future)
                {
                    if (sum > 0)
                    {
                        activeDays++;
                        totalRead += r;
                        totalListen += l;
                        runningStreak++;
                        if (runningStreak > longestStreak) longestStreak = runningStreak;
                    }
                    else
                    {
                        runningStreak = 0;
                    }
                }
            }
            weeks.Add(col);
        }

        // Current streak counts backwards from today (or yesterday if today has no activity yet).
        int currentStreak = 0;
        DateOnly cursor = today;
        if (!HasActivity(totals, cursor))
            cursor = today.AddDays(-1);
        while (cursor >= startMonday && HasActivity(totals, cursor))
        {
            currentStreak++;
            cursor = cursor.AddDays(-1);
        }

        return new ReadingHeatmapSummary(
            Weeks: weeks,
            RangeStart: startMonday,
            RangeEnd: today,
            ActiveDays: activeDays,
            TotalReadSeconds: totalRead,
            TotalListenSeconds: totalListen,
            CurrentStreak: currentStreak,
            LongestStreak: longestStreak);
    }

    private static bool HasActivity(Dictionary<string, (uint Read, uint Listen)> totals, DateOnly date) =>
        totals.TryGetValue(FormatDayKey(date), out var v) && (v.Read + v.Listen) > 0;
}
