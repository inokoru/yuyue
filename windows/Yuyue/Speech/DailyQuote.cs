using System.Net.Http;
using System.Text.Json;
using Yuyue.Core;

namespace Yuyue.Speech;

public sealed record DailyQuoteEntry(string Text, string Source = "");

/// <summary>
/// 每日一言 for voice auditioning on Windows:
/// pulls once per calendar day from Hitokoto (文学 / 诗词 / 哲学), cached via <see cref="LibraryService.Secrets"/>,
/// with curated literary lines ready immediately offline.
/// </summary>
public static class DailyQuote
{
    private const string HitokotoUrl = "https://v1.hitokoto.cn/?c=d&c=i&c=k&max_length=46";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2.5) };
    private static readonly HashSet<string> GenericSources = ["原创", "网络", "一言", "未知", "佚名", "网友", "其他", "自创"];

    private static readonly DailyQuoteEntry[] BuiltinZh =
    [
        new("江上之清风，与山间之明月，耳得之而为声，目遇之而成色。", "苏轼《赤壁赋》"),
        new("庭下如积水空明，水中藻、荇交横，盖竹柏影也。", "苏轼《记承天寺夜游》"),
        new("行到水穷处，坐看云起时。偶然值林叟，谈笑无还期。", "王维《终南别业》"),
        new("醉后不知天在水，满船清梦压星河。", "唐温如《题龙阳县青草湖》"),
        new("落霞与孤鹜齐飞，秋水共长天一色。", "王勃《滕王阁序》"),
        new("赌书消得泼茶香，当时只道是寻常。", "纳兰性德《浣溪沙》"),
        new("我见青山多妩媚，料青山见我应如是。", "辛弃疾《贺新郎》"),
        new("一个人只拥有此生此世是不够的，他还应该拥有诗意的世界。", "王小波《万寿寺》"),
        new("太阳，它每时每刻都是夕阳也都是旭日。", "史铁生《我与地坛》"),
        new("溪流如弓背，山路如弓弦，故远近有了小小差异。", "沈从文《边城》"),
        new("花开了，就像睡醒了似的。鸟飞了，就像在天上逛似的。", "萧红《呼兰河传》"),
        new("月光如流水一般，静静地泻在这一片叶子和花上。", "朱自清《荷塘月色》"),
        new("最妙的是下点小雪呀。看吧，山上的矮松越发的青黑，树尖上顶着一髻儿白花。", "老舍《济南的冬天》"),
        new("雾凇沆砀，天与云与山与水，上下一白。", "张岱《湖心亭看雪》"),
    ];

    private static DailyQuoteEntry? _cached;
    private static string _cachedDay = "";
    private static int _offset;

    private static string TodayKey() => DateTime.Now.ToString("yyyy-MM-dd");

    private static DailyQuoteEntry BuiltinForToday(int offset = 0)
    {
        int days = (int)DateTimeOffset.Now.ToUnixTimeSeconds() / 86_400;
        int idx = ((days + offset) % BuiltinZh.Length + BuiltinZh.Length) % BuiltinZh.Length;
        return BuiltinZh[idx];
    }

    public static DailyQuoteEntry Current(LibraryService? library = null)
    {
        string today = TodayKey();
        if (_cached is not null && (_cachedDay == today || _offset != 0))
            return _cached;

        if (library is not null && library.Secrets.Get("voice.daily.zh.date") == today)
        {
            string savedText = library.Secrets.Get("voice.daily.zh.text").Trim();
            if (!string.IsNullOrEmpty(savedText))
            {
                _cached = new DailyQuoteEntry(savedText, library.Secrets.Get("voice.daily.zh.source"));
                _cachedDay = today;
                return _cached;
            }
        }

        _cached = BuiltinForToday(_offset);
        _cachedDay = today;
        return _cached;
    }

    public static async Task<DailyQuoteEntry> EnsureTodayAsync(LibraryService library)
    {
        var cur = Current(library);
        string today = TodayKey();
        if (library.Secrets.Get("voice.daily.zh.date") == today && library.Secrets.Get("voice.daily.zh.online") == "1")
            return cur;

        var fetched = await FetchHitokotoAsync();
        if (fetched is null) return cur;

        Save(library, today, fetched, online: true);
        _cached = fetched;
        _cachedDay = today;
        return fetched;
    }

    public static async Task<DailyQuoteEntry> RefreshAsync(LibraryService library)
    {
        string prev = Current(library).Text;
        string today = TodayKey();
        var fetched = await FetchHitokotoAsync(prev);
        bool online = fetched is not null;
        var chosen = fetched ?? NextBuiltin(prev);
        Save(library, today, chosen, online);
        _cached = chosen;
        _cachedDay = today;
        return chosen;
    }

    private static DailyQuoteEntry NextBuiltin(string avoid)
    {
        _offset++;
        var next = BuiltinForToday(_offset);
        if (next.Text == avoid && BuiltinZh.Length > 1)
        {
            _offset++;
            next = BuiltinForToday(_offset);
        }
        return next;
    }

    private static void Save(LibraryService library, string today, DailyQuoteEntry entry, bool online)
    {
        library.Secrets.Set("voice.daily.zh.date", today);
        library.Secrets.Set("voice.daily.zh.text", entry.Text);
        library.Secrets.Set("voice.daily.zh.source", entry.Source);
        library.Secrets.Set("voice.daily.zh.online", online ? "1" : "0");
    }

    private static async Task<DailyQuoteEntry?> FetchHitokotoAsync(string? avoid = null)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, HitokotoUrl);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            req.Headers.TryAddWithoutValidation("User-Agent", "Yuyue/0.1");
            using var resp = await Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            string text = (root.TryGetProperty("hitokoto", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() : null)?.Trim() ?? "";
            if (text.Length is < 4 or > 80 || text.Contains("我冒了严寒", StringComparison.Ordinal)) return null;
            if (avoid is not null && text == avoid) return null;
            string? who = root.TryGetProperty("from_who", out var w) && w.ValueKind == JsonValueKind.String ? w.GetString() : null;
            string? from = root.TryGetProperty("from", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
            return new DailyQuoteEntry(text, FormatSource(who, from));
        }
        catch
        {
            return null;
        }
    }

    private static string FormatSource(string? rawWho, string? rawFrom)
    {
        string who = (rawWho ?? "").Trim();
        string from = (rawFrom ?? "").Trim();
        if (GenericSources.Contains(who)) who = "";
        if (GenericSources.Contains(from)) from = "";
        string wrappedFrom = string.IsNullOrEmpty(from)
            ? ""
            : (from.StartsWith('《') || from.Contains('·') ? from : $"《{from}》");
        if (!string.IsNullOrEmpty(who) && !string.IsNullOrEmpty(wrappedFrom))
            return who == from ? who : $"{who}{wrappedFrom}";
        return !string.IsNullOrEmpty(who) ? who : wrappedFrom;
    }
}
