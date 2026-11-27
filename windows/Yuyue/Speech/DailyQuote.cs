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
    private static readonly HttpClient Http = new() { Timeout = System.TimeSpan.FromSeconds(2.5) };
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

    private static readonly DailyQuoteEntry[] BuiltinEn =
    [
        new("I declare after all there is no enjoyment like reading!", "Jane Austen · Pride and Prejudice"),
        new("Whatever our souls are made of, his and mine are the same.", "Emily Brontë · Wuthering Heights"),
        new("I am no bird; and no net ensnares me: I am a free human being with an independent will.", "Charlotte Brontë · Jane Eyre"),
        new("The world is full of obvious things which nobody by any chance ever observes.", "Arthur Conan Doyle"),
        new("We are all in the gutter, but some of us are looking at the stars.", "Oscar Wilde"),
        new("To live is the rarest thing in the world. Most people exist, that is all.", "Oscar Wilde"),
        new("Only that day dawns to which we are awake. There is more day to dawn.", "Henry David Thoreau · Walden"),
        new("I went to the woods because I wished to live deliberately.", "Henry David Thoreau · Walden"),
        new("So we beat on, boats against the current, borne back ceaselessly into the past.", "F. Scott Fitzgerald · The Great Gatsby"),
        new("Mrs. Dalloway said she would buy the flowers herself.", "Virginia Woolf · Mrs Dalloway"),
        new("A room without books is like a body without a soul.", "Cicero"),
        new("Hope is the thing with feathers that perches in the soul, and sings the tune without the words.", "Emily Dickinson"),
        new("You have power over your mind — not outside events. Realize this, and you will find strength.", "Marcus Aurelius · Meditations"),
        new("It is a truth universally acknowledged, that a single man in possession of a good fortune, must be in want of a wife.", "Jane Austen · Pride and Prejudice"),
    ];

    private static readonly object Gate = new();
    private static readonly Dictionary<string, DailyQuoteEntry> CachedEntries = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> CachedDays = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, int> Offsets = new(StringComparer.Ordinal);

    private static string Norm(string? language) =>
        !string.IsNullOrWhiteSpace(language) && language.Trim().StartsWith("en", StringComparison.OrdinalIgnoreCase)
            ? "en"
            : "zh";

    private static DailyQuoteEntry[] Pool(string lang) => lang == "en" ? BuiltinEn : BuiltinZh;

    private static string TodayKey() => DateTime.Now.ToString("yyyy-MM-dd");

    private static DailyQuoteEntry BuiltinForToday(string lang, int offset = 0)
    {
        var list = Pool(lang);
        int days = (int)(DateTimeOffset.Now.ToUnixTimeSeconds() / 86_400);
        int idx = ((days + offset) % list.Length + list.Length) % list.Length;
        return list[idx];
    }

    public static DailyQuoteEntry Current(LibraryService? library = null, string? language = null)
    {
        string lang = Norm(language);
        string today = TodayKey();

        lock (Gate)
        {
            bool hasOffset = Offsets.TryGetValue(lang, out int offset) && offset != 0;
            if (CachedEntries.TryGetValue(lang, out var cached) &&
                ((CachedDays.TryGetValue(lang, out var cDay) && cDay == today) || hasOffset))
            {
                return cached;
            }

            if (library is not null && library.Secrets.Get($"voice.daily.{lang}.date") == today)
            {
                string savedText = library.Secrets.Get($"voice.daily.{lang}.text").Trim();
                if (!string.IsNullOrEmpty(savedText))
                {
                    var saved = new DailyQuoteEntry(savedText, library.Secrets.Get($"voice.daily.{lang}.source"));
                    CachedEntries[lang] = saved;
                    CachedDays[lang] = today;
                    return saved;
                }
            }

            var fallback = BuiltinForToday(lang, Offsets.GetValueOrDefault(lang, 0));
            CachedEntries[lang] = fallback;
            CachedDays[lang] = today;
            return fallback;
        }
    }

    public static async Task<DailyQuoteEntry> EnsureTodayAsync(LibraryService library, string? language = null)
    {
        string lang = Norm(language);
        var cur = Current(library, lang);
        if (lang != "zh") return cur;

        string today = TodayKey();
        if (library.Secrets.Get("voice.daily.zh.date") == today && library.Secrets.Get("voice.daily.zh.online") == "1")
            return cur;

        var fetched = await FetchHitokotoAsync();
        if (fetched is null) return cur;

        Save(library, "zh", today, fetched, online: true);
        lock (Gate)
        {
            CachedEntries["zh"] = fetched;
            CachedDays["zh"] = today;
        }
        return fetched;
    }

    public static async Task<DailyQuoteEntry> RefreshAsync(LibraryService library, string? language = null)
    {
        string lang = Norm(language);
        string prev = Current(library, lang).Text;
        string today = TodayKey();
        DailyQuoteEntry? fetched = null;
        if (lang == "zh")
        {
            fetched = await FetchHitokotoAsync(prev) ?? await FetchHitokotoAsync(prev);
        }
        bool online = fetched is not null;
        var chosen = fetched ?? NextBuiltin(lang, prev);
        Save(library, lang, today, chosen, online);
        lock (Gate)
        {
            CachedEntries[lang] = chosen;
            CachedDays[lang] = today;
        }
        return chosen;
    }

    private static DailyQuoteEntry NextBuiltin(string lang, string avoid)
    {
        var list = Pool(lang);
        lock (Gate)
        {
            int step = Offsets.GetValueOrDefault(lang, 0) + 1;
            var next = BuiltinForToday(lang, step);
            if (next.Text == avoid && list.Length > 1)
            {
                step++;
                next = BuiltinForToday(lang, step);
            }
            Offsets[lang] = step;
            return next;
        }
    }

    private static void Save(LibraryService library, string lang, string today, DailyQuoteEntry entry, bool online)
    {
        library.Secrets.Set($"voice.daily.{lang}.date", today);
        library.Secrets.Set($"voice.daily.{lang}.text", entry.Text);
        library.Secrets.Set($"voice.daily.{lang}.source", entry.Source);
        library.Secrets.Set($"voice.daily.{lang}.online", online ? "1" : "0");
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

    public static string PickBookOrFallbackLine(LibraryService library, string? language = null)
    {
        string lang = Norm(language);
        try
        {
            var kept = library.GetKeptBooks();
            foreach (var b in kept)
            {
                bool isEnBook = b.Title.All(ch => ch < 128) && b.Title.Any(char.IsLetter);
                if ((lang == "en") != isEnBook) continue;
                var marks = library.GetMarks(b.Id);
                var hl = marks.FirstOrDefault(n => !string.IsNullOrWhiteSpace(n.Text) && n.Text.Trim().Length is >= 8 and <= 96);
                if (hl is not null)
                    return hl.Text.Trim();
            }
        }
        catch { }
        return Current(library, lang).Text;
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
