using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace Yuyue.Core;

/// <summary>
/// Full zh-Hans ↔ English localization engine backed by <c>Assets/L10n/en.json</c>
/// (merged from Apple <c>Localizable.xcstrings</c>, Android <c>extra-en.json</c>, and Windows overrides),
/// plus compiled regex templates for dynamic counters/progress strings and bi-directional live visual-tree switching.
/// </summary>
public static partial class L10n
{
    public static bool IsEnglish { get; set; }

    private static readonly Lazy<Dictionary<string, string>> EnDict = new(LoadDictionary);

    private static readonly ConditionalWeakTable<object, Dictionary<string, string>> Originals = new();

    // Deferred nested holder so headless unit tests calling L10n.T() never initialize WinUI XAML DependencyProperty
    private static class XamlProps
    {
        public static readonly DependencyProperty SkipProperty = DependencyProperty.RegisterAttached(
            "Skip",
            typeof(bool),
            typeof(L10n),
            new PropertyMetadata(false));
    }

    public static int DictionaryCount => EnDict.Value.Count;

    /// <summary>
    /// Marks a visual element (and its subtree) as user data — book titles, authors, quotes, notes —
    /// so <see cref="ApplyTree"/> never rewrites it when switching UI languages.
    /// Uses a WinRT attached <see cref="DependencyProperty"/> so identity survives CsWinRT visual-tree projection.
    /// </summary>
    public static T Skip<T>(T element) where T : DependencyObject
    {
        element.SetValue(XamlProps.SkipProperty, true);
        return element;
    }

    public static string Tr(string zh, string en) => IsEnglish ? en : zh;

    /// <summary>
    /// Translates a Chinese UI string to English when <see cref="IsEnglish"/> is true,
    /// supporting exact dictionary hits, compiled dynamic templates, and multi-segment <c> · </c> strings.
    /// </summary>
    public static string T(string? zh)
    {
        if (string.IsNullOrEmpty(zh) || !IsEnglish) return zh ?? "";
        if (!ContainsCjk(zh)) return zh;

        string trimmed = zh.Trim();
        var dict = EnDict.Value;
        if (dict.TryGetValue(zh, out var exact)) return exact;
        if (trimmed.Length != zh.Length && dict.TryGetValue(trimmed, out var trimHit))
            return zh.Replace(trimmed, trimHit, StringComparison.Ordinal);

        // Leading/trailing bullet wrappers ("· 还剩 5 分", "图书 ·", "✓ 跟随系统")
        if (trimmed.StartsWith("· ", StringComparison.Ordinal))
        {
            string inner = trimmed[2..];
            string tInner = T(inner);
            if (tInner != inner) return "· " + tInner;
        }
        if (trimmed.EndsWith(" ·", StringComparison.Ordinal))
        {
            string inner = trimmed[..^2];
            string tInner = T(inner);
            if (tInner != inner) return tInner + " ·";
        }
        if (trimmed.StartsWith("✓ ", StringComparison.Ordinal))
        {
            string inner = trimmed[2..];
            string tInner = T(inner);
            if (tInner != inner) return "✓ " + tInner;
        }

        // Strip trailing ellipsis / colon variants
        if (trimmed.EndsWith('…') && dict.TryGetValue(trimmed[..^1], out var noEllipsis))
            return noEllipsis + "…";

        // Dynamic formatted patterns
        string? dyn = TryTranslateDynamic(trimmed);
        if (dyn is not null) return dyn;

        // Multi-segment "A · B · C" translation (e.g. "纸本 · 系统 · 18", "图书 · 33% · 还剩 26 小时 54 分")
        if (trimmed.Contains(" · ", StringComparison.Ordinal))
        {
            string[] parts = trimmed.Split(" · ");
            bool changed = false;
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i];
                if (!ContainsCjk(p)) continue;
                string tp = TranslateSingleSegment(p);
                if (tp != p) { parts[i] = tp; changed = true; }
            }
            if (changed) return string.Join(" · ", parts);
        }

        return zh;
    }

    private static string TranslateSingleSegment(string seg)
    {
        var dict = EnDict.Value;
        if (dict.TryGetValue(seg, out var hit)) return hit;
        return TryTranslateDynamic(seg) ?? seg;
    }

    private static string? TryTranslateDynamic(string s)
    {
        var m = RxOtherBooks().Match(s);
        if (m.Success) return $"Other {m.Groups[1].Value}";

        m = RxAllBooks().Match(s);
        if (m.Success) return $"All {m.Groups[1].Value}";

        m = RxBookCount().Match(s);
        if (m.Success) return m.Groups[1].Value == "1" ? "1 book" : $"{m.Groups[1].Value} books";

        m = RxHighlightCount().Match(s);
        if (m.Success) return m.Groups[1].Value == "1" ? "1 highlight" : $"{m.Groups[1].Value} highlights";

        m = RxTabHighlights().Match(s);
        if (m.Success) return $"Highlights {m.Groups[1].Value}";

        m = RxTabNotes().Match(s);
        if (m.Success) return $"Notes {m.Groups[1].Value}";

        m = RxTabWords().Match(s);
        if (m.Success) return $"Words {m.Groups[1].Value}";

        m = RxPageThis().Match(s);
        if (m.Success) return $"Page {m.Groups[1].Value} · This page";

        m = RxPageRangeOf().Match(s);
        if (m.Success) return $"Pages {m.Groups[1].Value}–{m.Groups[2].Value} / {m.Groups[3].Value}";

        m = RxPageOf().Match(s);
        if (m.Success) return $"Page {m.Groups[1].Value} / {m.Groups[2].Value}";

        m = RxPageSingle().Match(s);
        if (m.Success) return $"Page {m.Groups[1].Value}";

        m = RxEdition().Match(s);
        if (m.Success) return $"Edition {m.Groups[1].Value}";

        m = RxPagesTotal().Match(s);
        if (m.Success) return m.Groups[1].Value == "1" ? "1 page" : $"{m.Groups[1].Value} pages";

        m = RxSentenceOf().Match(s);
        if (m.Success) return $"Sentence {m.Groups[1].Value} / {m.Groups[2].Value}";

        m = RxPageRemainingMin().Match(s);
        if (m.Success) return $"{m.Groups[1].Value} min left on this page";

        m = RxChapterRemainingMin().Match(s);
        if (m.Success) return $"Chapter: {m.Groups[1].Value} min left";

        m = RxRemainingHoursMin().Match(s);
        if (m.Success) return $"{m.Groups[1].Value}h {m.Groups[2].Value}m left";

        m = RxRemainingHours().Match(s);
        if (m.Success) return $"{m.Groups[1].Value}h left";

        m = RxRemainingMin().Match(s);
        if (m.Success) return $"{m.Groups[1].Value} min left";

        m = RxNoSpoilersPage().Match(s);
        if (m.Success) return $"Up to page {m.Groups[1].Value} · No spoilers";

        m = RxStorageLegend().Match(s);
        if (m.Success)
        {
            string lbl = m.Groups[1].Value switch
            {
                "书文件" => "Books",
                "有声书录音" => "Audio",
                "语音模型" => "Models",
                "已删除" => "Deleted",
                _ => "Other",
            };
            return $"{lbl} {m.Groups[2].Value}";
        }

        m = RxDiskAvailable().Match(s);
        if (m.Success) return $"{m.Groups[1].Value} free";

        m = RxSnapshotEst().Match(s);
        if (m.Success) return $"Estimated snapshot size {m.Groups[1].Value}";

        m = RxDownloadingPct().Match(s);
        if (m.Success) return $"Downloading {m.Groups[1].Value}%";

        m = RxMatchesCount().Match(s);
        if (m.Success) return m.Groups[1].Value == "1" ? "1 match" : $"{m.Groups[1].Value} matches";

        m = RxQaCache().Match(s);
        if (m.Success) return $"Companion Q&A Cache ({m.Groups[1].Value})";

        m = RxSyncFilesSub().Match(s);
        if (m.Success) return $"{m.Groups[1].Value} · Uses your cloud storage; podcast episodes are tiny and always sync";

        m = RxSyncFilesLoading().Match(s);
        if (m.Success) return "Calculating book file size… · Uses your cloud storage; podcast episodes always sync";

        m = RxLangSub().Match(s);
        if (m.Success) return $"Current: {m.Groups[1].Value} · Switching restarts the app and returns here";

        m = RxLangSysSub().Match(s);
        if (m.Success) return $"Follow System (Current: {m.Groups[1].Value}) · Switching restarts the app and returns here";

        m = RxSwitchToLang().Match(s);
        if (m.Success) return $"Switch to {m.Groups[1].Value}?";

        m = RxDiscoveredBook().Match(s);
        if (m.Success) return $"Found ebook \"{m.Groups[1].Value}\"";

        m = RxMonthDay().Match(s);
        if (m.Success) return $"{m.Groups[1].Value}/{m.Groups[2].Value}";

        return null;
    }

    /// <summary>
    /// Recursively applies the current <see cref="IsEnglish"/> language to a WinUI 3 visual tree,
    /// preserving original Chinese strings in a <see cref="ConditionalWeakTable{TKey,TValue}"/> so
    /// switching Chinese ↔ English ↔ Chinese live works cleanly without restarting windows.
    /// </summary>
    public static void ApplyTree(DependencyObject? root)
    {
        if (root is null) return;
        ApplyNode(root);
    }

    private static void ApplyNode(DependencyObject node)
    {
        if (node.GetValue(XamlProps.SkipProperty) is true) return;

        if (node is TextBlock tb)
        {
            if (tb.Inlines.Count == 0 || (tb.Inlines.Count == 1 && tb.Inlines[0] is Run))
            {
                string orig = GetOrStoreOriginal(tb, "Text", tb.Text);
                string next = T(orig);
                if (tb.Text != next) tb.Text = next;
            }
            else
            {
                foreach (var inl in tb.Inlines)
                {
                    if (inl is Run run)
                    {
                        string orig = GetOrStoreOriginal(run, "Text", run.Text);
                        string next = T(orig);
                        if (run.Text != next) run.Text = next;
                    }
                }
            }
        }
        else if (node is TextBox tbox)
        {
            if (!string.IsNullOrEmpty(tbox.PlaceholderText))
            {
                string orig = GetOrStoreOriginal(tbox, "PlaceholderText", tbox.PlaceholderText);
                string next = T(orig);
                if (tbox.PlaceholderText != next) tbox.PlaceholderText = next;
            }
            if (tbox.Header is string hdr)
            {
                string orig = GetOrStoreOriginal(tbox, "Header", hdr);
                string next = T(orig);
                if (hdr != next) tbox.Header = next;
            }
        }
        else if (node is PasswordBox pbox)
        {
            if (!string.IsNullOrEmpty(pbox.PlaceholderText))
            {
                string orig = GetOrStoreOriginal(pbox, "PlaceholderText", pbox.PlaceholderText);
                string next = T(orig);
                if (pbox.PlaceholderText != next) pbox.PlaceholderText = next;
            }
            if (pbox.Header is string hdr)
            {
                string orig = GetOrStoreOriginal(pbox, "Header", hdr);
                string next = T(orig);
                if (hdr != next) pbox.Header = next;
            }
        }
        else if (node is AutoSuggestBox asb)
        {
            if (!string.IsNullOrEmpty(asb.PlaceholderText))
            {
                string orig = GetOrStoreOriginal(asb, "PlaceholderText", asb.PlaceholderText);
                string next = T(orig);
                if (asb.PlaceholderText != next) asb.PlaceholderText = next;
            }
            if (asb.Header is string hdr)
            {
                string orig = GetOrStoreOriginal(asb, "Header", hdr);
                string next = T(orig);
                if (hdr != next) asb.Header = next;
            }
        }
        else if (node is ComboBox cb)
        {
            if (!string.IsNullOrEmpty(cb.PlaceholderText))
            {
                string orig = GetOrStoreOriginal(cb, "PlaceholderText", cb.PlaceholderText);
                string next = T(orig);
                if (cb.PlaceholderText != next) cb.PlaceholderText = next;
            }
            if (cb.Header is string hdr)
            {
                string orig = GetOrStoreOriginal(cb, "Header", hdr);
                string next = T(orig);
                if (hdr != next) cb.Header = next;
            }
            foreach (object item in cb.Items)
            {
                if (item is DependencyObject itemDep) ApplyNode(itemDep);
            }
        }
        else if (node is ToggleSwitch ts)
        {
            if (ts.Header is string hdr)
            {
                string orig = GetOrStoreOriginal(ts, "Header", hdr);
                string next = T(orig);
                if (hdr != next) ts.Header = next;
            }
            if (ts.OnContent is string onStr && !string.IsNullOrEmpty(onStr))
            {
                string orig = GetOrStoreOriginal(ts, "OnContent", onStr);
                string next = T(orig);
                if (onStr != next) ts.OnContent = next;
            }
            if (ts.OffContent is string offStr && !string.IsNullOrEmpty(offStr))
            {
                string orig = GetOrStoreOriginal(ts, "OffContent", offStr);
                string next = T(orig);
                if (offStr != next) ts.OffContent = next;
            }
        }
        else if (node is ContentControl cc)
        {
            if (cc.Content is string str)
            {
                string orig = GetOrStoreOriginal(cc, "Content", str);
                string next = T(orig);
                if (str != next) cc.Content = next;
            }
            else if (cc.Content is DependencyObject childObj && VisualTreeHelper.GetChildrenCount(cc) == 0)
            {
                ApplyNode(childObj);
            }
        }

        if (node is FrameworkElement fe)
        {
            if (ToolTipService.GetToolTip(fe) is string tip && !string.IsNullOrEmpty(tip))
            {
                string orig = GetOrStoreOriginal(fe, "ToolTip", tip);
                string next = T(orig);
                if (tip != next) ToolTipService.SetToolTip(fe, next);
            }
            if (fe.ContextFlyout is FlyoutBase ctx) ApplyFlyout(ctx);
        }

        if (node is Button btn && btn.Flyout is FlyoutBase fly)
            ApplyFlyout(fly);

        if (node is ItemsRepeater ir && ir.ItemsSource is System.Collections.IEnumerable items)
        {
            foreach (object item in items)
            {
                if (item is DependencyObject itemDep) ApplyNode(itemDep);
            }
        }

        // Walk logical children first when visual tree is not yet realized (e.g. collapsed panels)
        int vCount = VisualTreeHelper.GetChildrenCount(node);
        if (vCount > 0)
        {
            for (int i = 0; i < vCount; i++)
                ApplyNode(VisualTreeHelper.GetChild(node, i));
        }
        else if (node is Panel panel)
        {
            foreach (var child in panel.Children) ApplyNode(child);
        }
        else if (node is Border border && border.Child is not null)
        {
            ApplyNode(border.Child);
        }
        else if (node is ContentPresenter cp && cp.Content is DependencyObject cpChild)
        {
            ApplyNode(cpChild);
        }
        else if (node is ScrollViewer sv && sv.Content is DependencyObject svChild)
        {
            ApplyNode(svChild);
        }
    }

    private static void ApplyFlyout(FlyoutBase flyout)
    {
        if (flyout is MenuFlyout mf)
        {
            foreach (var item in mf.Items) ApplyMenuItem(item);
        }
        else if (flyout is Flyout f && f.Content is DependencyObject content)
        {
            ApplyNode(content);
        }
    }

    private static void ApplyMenuItem(MenuFlyoutItemBase item)
    {
        if (item is MenuFlyoutItem mi)
        {
            string orig = GetOrStoreOriginal(mi, "Text", mi.Text);
            string next = T(orig);
            if (mi.Text != next) mi.Text = next;
        }
        else if (item is MenuFlyoutSubItem sub)
        {
            string orig = GetOrStoreOriginal(sub, "Text", sub.Text);
            string next = T(orig);
            if (sub.Text != next) sub.Text = next;
            foreach (var child in sub.Items) ApplyMenuItem(child);
        }
    }

    private static string GetOrStoreOriginal(object target, string prop, string current)
    {
        var map = Originals.GetOrCreateValue(target);
        if (string.IsNullOrEmpty(current))
        {
            map.Remove(prop);
            return "";
        }
        if (!map.TryGetValue(prop, out var orig) || string.IsNullOrEmpty(orig))
        {
            map[prop] = current;
            return current;
        }
        // If code explicitly wrote a brand-new Chinese string OR updated a non-CJK counter/symbol ("5", "2 ›"),
        // treat the live value as authoritative instead of reverting to a stale cached value.
        if ((ContainsCjk(current) && current != orig) || (!ContainsCjk(orig) && !ContainsCjk(current) && current != orig))
        {
            map[prop] = current;
            return current;
        }
        return orig;
    }

    private static bool ContainsCjk(string s)
    {
        foreach (char ch in s)
        {
            if ((ch >= '\u4E00' && ch <= '\u9FFF') || (ch >= '\u3400' && ch <= '\u4DBF'))
                return true;
        }
        return false;
    }

    private static Dictionary<string, string> LoadDictionary()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "Assets", "L10n", "en.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "windows", "Yuyue", "Assets", "L10n", "en.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "Assets", "L10n", "en.json"),
        ];

        foreach (string path in candidates)
        {
            if (!File.Exists(path)) continue;
            try
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
                if (parsed is not null)
                {
                    foreach (var kv in parsed) map[kv.Key] = kv.Value;
                    break;
                }
            }
            catch { }
        }
        return map;
    }

    [GeneratedRegex(@"^其余\s*(\d+)\s*本$")]
    private static partial Regex RxOtherBooks();

    [GeneratedRegex(@"^全部\s*(\d+)\s*本$")]
    private static partial Regex RxAllBooks();

    [GeneratedRegex(@"^(\d+)\s*本$")]
    private static partial Regex RxBookCount();

    [GeneratedRegex(@"^(\d+)\s*条划线$")]
    private static partial Regex RxHighlightCount();

    [GeneratedRegex(@"^划线\s*(\d+)$")]
    private static partial Regex RxTabHighlights();

    [GeneratedRegex(@"^想法\s*(\d+)$")]
    private static partial Regex RxTabNotes();

    [GeneratedRegex(@"^生词\s*(\d+)$")]
    private static partial Regex RxTabWords();

    [GeneratedRegex(@"^第\s*(\d+)\s*页\s*·\s*这一页$")]
    private static partial Regex RxPageThis();

    [GeneratedRegex(@"^第\s*(\d+)[–\-](\d+)\s*/\s*(\d+)\s*页$")]
    private static partial Regex RxPageRangeOf();

    [GeneratedRegex(@"^第\s*(\d+)\s*/\s*(\d+)\s*页$")]
    private static partial Regex RxPageOf();

    [GeneratedRegex(@"^第\s*(\d+)\s*页$")]
    private static partial Regex RxPageSingle();

    [GeneratedRegex(@"^第\s*(\d+)\s*版$")]
    private static partial Regex RxEdition();

    [GeneratedRegex(@"^(\d+)\s*页$")]
    private static partial Regex RxPagesTotal();

    [GeneratedRegex(@"^第\s*(\d+)\s*/\s*(\d+)\s*句$")]
    private static partial Regex RxSentenceOf();

    [GeneratedRegex(@"^这一页还剩\s*(\d+)\s*分钟?$")]
    private static partial Regex RxPageRemainingMin();

    [GeneratedRegex(@"^本章还剩\s*(\d+)\s*分钟?$")]
    private static partial Regex RxChapterRemainingMin();

    [GeneratedRegex(@"^还剩\s*(\d+)\s*小时\s*(\d+)\s*分钟?$")]
    private static partial Regex RxRemainingHoursMin();

    [GeneratedRegex(@"^还剩\s*(\d+)\s*小时$")]
    private static partial Regex RxRemainingHours();

    [GeneratedRegex(@"^还剩\s*(\d+)\s*分钟?$")]
    private static partial Regex RxRemainingMin();

    [GeneratedRegex(@"^截至第\s*(\d+)\s*页，不涉及后续情节$")]
    private static partial Regex RxNoSpoilersPage();

    [GeneratedRegex(@"^(书文件|有声书录音|语音模型|已删除|其他)\s+(.+)$")]
    private static partial Regex RxStorageLegend();

    [GeneratedRegex(@"^本机可用\s+(.+)$")]
    private static partial Regex RxDiskAvailable();

    [GeneratedRegex(@"^预计快照大小\s+(.+)$")]
    private static partial Regex RxSnapshotEst();

    [GeneratedRegex(@"^下载中\s*(\d+)%$")]
    private static partial Regex RxDownloadingPct();

    [GeneratedRegex(@"^(\d+)\s*处$")]
    private static partial Regex RxMatchesCount();

    [GeneratedRegex(@"^书伴问答缓存（(.+)）$")]
    private static partial Regex RxQaCache();

    [GeneratedRegex(@"^(.+?)\s*·\s*占用你的.+空间；播客单集体积很小，始终同步$")]
    private static partial Regex RxSyncFilesSub();

    [GeneratedRegex(@"^正在计算书文件大小.*始终同步$")]
    private static partial Regex RxSyncFilesLoading();

    [GeneratedRegex(@"^当前：(.+?)\s*·\s*切换需确认重开，并回到当前页$")]
    private static partial Regex RxLangSub();

    [GeneratedRegex(@"^跟随系统（当前：(.+?)）\s*·\s*切换需确认重开，并回到当前页$")]
    private static partial Regex RxLangSysSub();

    [GeneratedRegex(@"^切换为「(.+?)」？$")]
    private static partial Regex RxSwitchToLang();

    [GeneratedRegex(@"^发现《(.+?)》电子书$")]
    private static partial Regex RxDiscoveredBook();

    [GeneratedRegex(@"^(\d{1,2})月(\d{1,2})日$")]
    private static partial Regex RxMonthDay();
}
