using System.Globalization;
using System.Text;

namespace Yuyue.Core;

/// <summary>
/// Matches titles, authors, and collection names by substring, word initials, and Mandarin Pinyin initials
/// (1:1 <c>NameMatch.swift</c> — e.g. <c>"st"</c> matches <c>"三体"</c>, <c>"hlm"</c> matches <c>"红楼梦"</c>,
/// <c>"hp"</c> matches <c>"Harry Potter"</c>).
/// Uses Windows <c>zh-CN</c> Pinyin collation boundaries so all CJK characters resolve with zero extra dependencies.
/// </summary>
public static class NameMatcher
{
    private static readonly CompareInfo ZhPinyinCompare = CultureInfo.GetCultureInfo("zh-CN").CompareInfo;

    // 23 Mandarin Pinyin initial boundaries sorted in zh-CN Pinyin collation order (no i/u/v initials in Pinyin).
    private static readonly (string FirstChar, char Initial)[] PinyinBuckets =
    [
        ("阿", 'a'),
        ("八", 'b'),
        ("擦", 'c'),
        ("搭", 'd'),
        ("蛾", 'e'),
        ("发", 'f'),
        ("噶", 'g'),
        ("哈", 'h'),
        ("击", 'j'),
        ("喀", 'k'),
        ("拉", 'l'),
        ("妈", 'm'),
        ("拿", 'n'),
        ("哦", 'o'),
        ("啪", 'p'),
        ("期", 'q'),
        ("然", 'r'),
        ("撒", 's'),
        ("塌", 't'),
        ("挖", 'w'),
        ("昔", 'x'),
        ("压", 'y'),
        ("匝", 'z'),
    ];

    public static bool Matches(string? text, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string q = query.Trim();
        if (text.Contains(q, StringComparison.OrdinalIgnoreCase)) return true;

        // Strip spaces for compact Pinyin/initial matching (e.g., "bn gd" -> "bngd")
        string compactQ = StripWhitespaceAndPunct(q).ToLowerInvariant();
        if (compactQ.Length == 0) return true;

        string compactText = StripWhitespaceAndPunct(text);
        if (compactText.Contains(compactQ, StringComparison.OrdinalIgnoreCase)) return true;

        // Only run Pinyin/word-initial matching when query is ASCII letters/digits
        if (compactQ.All(c => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')))
        {
            string initials = ExtractInitials(text);
            if (initials.Contains(compactQ, StringComparison.OrdinalIgnoreCase)) return true;
            if (IsSubsequence(compactQ, initials)) return true;
        }

        return false;
    }

    public static string ExtractInitials(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        bool inAsciiWord = false;
        foreach (char ch in text)
        {
            if (ch >= 0x4E00 && ch <= 0x9FFF)
            {
                inAsciiWord = false;
                char init = GetPinyinInitial(ch);
                if (init != '\0') sb.Append(init);
            }
            else if ((ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z'))
            {
                if (!inAsciiWord)
                {
                    sb.Append(char.ToLowerInvariant(ch));
                    inAsciiWord = true;
                }
            }
            else if (ch >= '0' && ch <= '9')
            {
                inAsciiWord = false;
                sb.Append(ch);
            }
            else
            {
                inAsciiWord = false;
            }
        }
        return sb.ToString();
    }

    public static char GetPinyinInitial(char ch)
    {
        if (ch < 0x4E00 || ch > 0x9FFF) return '\0';
        string s = ch.ToString();
        if (ZhPinyinCompare.Compare(s, PinyinBuckets[0].FirstChar, CompareOptions.None) < 0)
            return 'a';

        int lo = 0;
        int hi = PinyinBuckets.Length - 1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            int cmp = ZhPinyinCompare.Compare(s, PinyinBuckets[mid].FirstChar, CompareOptions.None);
            if (cmp == 0) return PinyinBuckets[mid].Initial;
            if (cmp > 0) lo = mid + 1;
            else hi = mid - 1;
        }
        return PinyinBuckets[Math.Clamp(hi, 0, PinyinBuckets.Length - 1)].Initial;
    }

    private static string StripWhitespaceAndPunct(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (!char.IsWhiteSpace(c) && !char.IsPunctuation(c)) sb.Append(c);
        }
        return sb.ToString();
    }

    private static bool IsSubsequence(string needle, string haystack)
    {
        if (needle.Length > haystack.Length) return false;
        int j = 0;
        for (int i = 0; i < haystack.Length && j < needle.Length; i++)
        {
            if (char.ToLowerInvariant(haystack[i]) == char.ToLowerInvariant(needle[j])) j++;
        }
        return j == needle.Length;
    }
}
