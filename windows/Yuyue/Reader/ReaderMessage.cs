using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yuyue.Reader;

/// <summary>
/// A message posted by the TypeScript reader (<c>reader/src/bridge.ts</c>, <c>docs/dev/reader-contract.md §5</c>).
/// </summary>
public sealed class ReaderMessage
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("section")]
    public uint? Section { get; set; }

    [JsonPropertyName("pageCount")]
    public int? PageCount { get; set; }

    [JsonPropertyName("pageWidth")]
    public double? PageWidth { get; set; }

    [JsonPropertyName("pageHeight")]
    public double? PageHeight { get; set; }

    [JsonPropertyName("sentencePages")]
    public int[]? SentencePages { get; set; }

    [JsonPropertyName("pageFirstSentence")]
    public int[]? PageFirstSentence { get; set; }

    [JsonPropertyName("x")]
    public double? X { get; set; }

    [JsonPropertyName("y")]
    public double? Y { get; set; }

    [JsonPropertyName("width")]
    public double? Width { get; set; }

    [JsonPropertyName("height")]
    public double? Height { get; set; }

    [JsonPropertyName("sentence")]
    public int? Sentence { get; set; }

    [JsonPropertyName("link")]
    public ReaderLink? Link { get; set; }

    [JsonPropertyName("mark")]
    public string? Mark { get; set; }

    [JsonPropertyName("word")]
    public ReaderWord? Word { get; set; }

    [JsonPropertyName("start")]
    public ReaderPoint? Start { get; set; }

    [JsonPropertyName("end")]
    public ReaderPoint? End { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("rect")]
    public ReaderRect? Rect { get; set; }

    [JsonPropertyName("page")]
    public int? Page { get; set; }

    [JsonPropertyName("progress")]
    public double? Progress { get; set; }

    [JsonPropertyName("level")]
    public string? Level { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("line")]
    public int? Line { get; set; }

    [JsonPropertyName("choice")]
    public string? Choice { get; set; }

    [JsonPropertyName("offset")]
    public int? Offset { get; set; }

    [JsonPropertyName("deltaY")]
    public double? DeltaY { get; set; }

    [JsonPropertyName("ctrl")]
    public bool? Ctrl { get; set; }

    public static ReaderMessage? Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ReaderMessage>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }
}

public sealed class ReaderLink
{
    [JsonPropertyName("target")]
    public string? Target { get; set; }

    [JsonPropertyName("external")]
    public string? External { get; set; }

    [JsonPropertyName("note")]
    public ReaderNoteRef? Note { get; set; }
}

public sealed class ReaderNoteRef
{
    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("before")]
    public string Before { get; set; } = "";
}

public sealed class ReaderPoint
{
    [JsonPropertyName("s")]
    public int S { get; set; }

    [JsonPropertyName("o")]
    public int O { get; set; }
}

public sealed class ReaderRect
{
    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    public double Y { get; set; }

    [JsonPropertyName("width")]
    public double Width { get; set; }

    [JsonPropertyName("height")]
    public double Height { get; set; }
}

public sealed class ReaderWord
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("start")]
    public ReaderPoint Start { get; set; } = new();

    [JsonPropertyName("end")]
    public ReaderPoint End { get; set; } = new();

    [JsonPropertyName("sentence")]
    public string Sentence { get; set; } = "";

    [JsonPropertyName("rect")]
    public ReaderRect Rect { get; set; } = new();
}

/// <summary>
/// Geometry of a laid-out section reported by <c>ready</c> / <c>layout</c>.
/// Matches Apple's <c>SectionLayout</c>.
/// </summary>
public sealed class SectionLayout
{
    public int PageCount { get; }
    public int[] SentencePages { get; }
    public int[] PageFirstSentence { get; }

    public SectionLayout(int pageCount, int[]? sentencePages, int[]? pageFirstSentence)
    {
        PageCount = Math.Max(1, pageCount);
        SentencePages = sentencePages ?? [];
        PageFirstSentence = pageFirstSentence ?? [];
    }

    public int PageOf(int sentence) =>
        sentence >= 0 && sentence < SentencePages.Length ? SentencePages[sentence] : 0;

    public int FirstSentenceOn(int page)
    {
        int p = Math.Min(page, PageFirstSentence.Length - 1);
        while (p >= 0 && PageFirstSentence[p] < 0) p--;
        if (p < 0) return 0;

        int first = PageFirstSentence[p];
        int s = first;
        while (s >= 0 && s < SentencePages.Length && SentencePages[s] <= p)
        {
            if (SentencePages[s] == p) return s;
            s++;
        }
        return first;
    }
}
