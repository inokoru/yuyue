using System.Globalization;
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

        m = RxFullDateYmd().Match(s);
        if (m.Success)
        {
            if (int.TryParse(m.Groups[1].Value, out int yr) &&
                int.TryParse(m.Groups[2].Value, out int mo) &&
                int.TryParse(m.Groups[3].Value, out int dy) &&
                mo is >= 1 and <= 12 && dy is >= 1 and <= 31)
            {
                try { return new DateTime(yr, mo, dy).ToString("MMM d, yyyy", CultureInfo.InvariantCulture); }
                catch { }
            }
            return $"{m.Groups[1].Value}-{m.Groups[2].Value}-{m.Groups[3].Value}";
        }

        m = RxYearMonth().Match(s);
        if (m.Success)
        {
            if (int.TryParse(m.Groups[1].Value, out int yr) &&
                int.TryParse(m.Groups[2].Value, out int mo) &&
                mo is >= 1 and <= 12)
            {
                try { return new DateTime(yr, mo, 1).ToString("MMM yyyy", CultureInfo.InvariantCulture); }
                catch { }
            }
            return $"{m.Groups[1].Value}-{m.Groups[2].Value}";
        }

        m = RxMonthDay().Match(s);
        if (m.Success)
        {
            if (int.TryParse(m.Groups[1].Value, out int mo) &&
                int.TryParse(m.Groups[2].Value, out int dy) &&
                mo is >= 1 and <= 12 && dy is >= 1 and <= 31)
            {
                try { return new DateTime(2026, mo, dy).ToString("MMM d", CultureInfo.InvariantCulture); }
                catch { }
            }
            return $"{m.Groups[1].Value}/{m.Groups[2].Value}";
        }

        m = RxNotesSummary().Match(s);
        if (m.Success)
        {
            string n = m.Groups[1].Value;
            string b = m.Groups[2].Value;
            return $"{n} {(n == "1" ? "note" : "notes")} · from {b} {(b == "1" ? "book" : "books")}";
        }

        m = RxItemsCount().Match(s);
        if (m.Success) return m.Groups[1].Value == "1" ? "1 note" : $"{m.Groups[1].Value} notes";

        m = RxFromBooksCount().Match(s);
        if (m.Success) return m.Groups[1].Value == "1" ? "from 1 book" : $"from {m.Groups[1].Value} books";

        m = RxReadUpToChapter().Match(s);
        if (m.Success) return $"At Ch. {m.Groups[1].Value}";

        m = RxChapterPct().Match(s);
        if (m.Success) return $"Ch. {m.Groups[1].Value}%";

        m = RxStatusChipCount().Match(s);
        if (m.Success) return $"{T(m.Groups[1].Value)} {m.Groups[2].Value}";

        m = RxWeekday().Match(s);
        if (m.Success)
        {
            return m.Groups[1].Value switch
            {
                "一" => "Monday",
                "二" => "Tuesday",
                "三" => "Wednesday",
                "四" => "Thursday",
                "五" => "Friday",
                _ => "Sunday",
            };
        }
        if (s == "星期六" || s == "周六") return "Saturday";

        // Sync & relative time
        m = RxSyncMinutesAgo().Match(s);
        if (m.Success) return $"Synced {m.Groups[1].Value} min ago";

        m = RxSyncTodayAt().Match(s);
        if (m.Success) return $"Synced today at {m.Groups[1].Value}";

        m = RxSyncDateAt().Match(s);
        if (m.Success) return $"Synced {m.Groups[1].Value}";

        m = RxPlacePendingSync().Match(s);
        if (m.Success) return $"{T(m.Groups[1].Value)} · Pending sync";

        m = RxPlaceJustSynced().Match(s);
        if (m.Success) return $"{T(m.Groups[1].Value)} · Just synced";

        m = RxMinutesAgo().Match(s);
        if (m.Success) return $"{m.Groups[1].Value} min ago";

        m = RxHoursAgo().Match(s);
        if (m.Success) return $"{m.Groups[1].Value}h ago";

        m = RxDaysAgo().Match(s);
        if (m.Success) return $"{m.Groups[1].Value}d ago";

        m = RxDevicesCount().Match(s);
        if (m.Success) return m.Groups[1].Value == "1" ? "1 device" : $"{m.Groups[1].Value} devices";

        // Shelf & Review
        m = RxKeptItems().Match(s);
        if (m.Success) return $"Kept {m.Groups[1].Value}";

        m = RxAddedToCollection().Match(s);
        if (m.Success) return $"Added to \"{m.Groups[1].Value}\"";

        m = RxPutIntoCollection().Match(s);
        if (m.Success) return $"Add to \"{m.Groups[1].Value}\"";

        m = RxImportedBooks().Match(s);
        if (m.Success) return m.Groups[1].Value == "1" ? "Imported 1 book" : $"Imported {m.Groups[1].Value} books";

        m = RxTotalBooks().Match(s);
        if (m.Success) return m.Groups[1].Value == "1" ? "1 book total" : $"{m.Groups[1].Value} books total";

        m = RxImportingProgress().Match(s);
        if (m.Success) return $"Importing {m.Groups[1].Value} / {m.Groups[2].Value}…";

        m = RxDropToImportFiles().Match(s);
        if (m.Success) return m.Groups[1].Value == "1" ? "Drop to import 1 file" : $"Drop to import {m.Groups[1].Value} files";

        // Progress & Reading
        m = RxReadDays().Match(s);
        if (m.Success) return m.Groups[1].Value == "1" ? "Read 1 day" : $"Read {m.Groups[1].Value} days";

        m = RxListenedHoursMin().Match(s);
        if (m.Success) return $"Listened {m.Groups[1].Value}h {m.Groups[2].Value}m";

        m = RxListenedHours().Match(s);
        if (m.Success) return $"Listened {m.Groups[1].Value}h";

        m = RxListenedMinutes().Match(s);
        if (m.Success) return $"Listened {m.Groups[1].Value} min";

        m = RxChapterOf().Match(s);
        if (m.Success) return $"Chapter {m.Groups[1].Value} / {m.Groups[2].Value}";

        m = RxChapterSingle().Match(s);
        if (m.Success) return $"Chapter {m.Groups[1].Value}";

        m = RxEpisodeSingle().Match(s);
        if (m.Success) return $"Episode {m.Groups[1].Value}";

        m = RxApproxHoursMin().Match(s);
        if (m.Success) return $"~{m.Groups[1].Value}h {m.Groups[2].Value}m";

        m = RxApproxMinutes().Match(s);
        if (m.Success) return $"~{m.Groups[1].Value} min";

        m = RxDurationHoursMin().Match(s);
        if (m.Success) return $"{m.Groups[1].Value}h {m.Groups[2].Value}m";

        m = RxDurationMinutes().Match(s);
        if (m.Success) return $"{m.Groups[1].Value} min";

        m = RxBookRemainingHoursMin().Match(s);
        if (m.Success) return $"{m.Groups[1].Value}h {m.Groups[2].Value}m left in book";

        m = RxBookRemainingMin().Match(s);
        if (m.Success) return $"{m.Groups[1].Value} min left in book";

        m = RxRemainingClock().Match(s);
        if (m.Success) return $"{m.Groups[1].Value} left";

        m = RxSleepCountdown().Match(s);
        if (m.Success) return $"Sleep in {m.Groups[1].Value}";

        m = RxSleepFading().Match(s);
        if (m.Success) return $"Fading out in {m.Groups[1].Value}s (Space +10m)";

        m = RxCharCount().Match(s);
        if (m.Success) return m.Groups[1].Value == "1" ? "1 char" : $"{m.Groups[1].Value} chars";

        // Settings, Downloads, Streaming & Audio
        m = RxDownloadAndUseMb().Match(s);
        if (m.Success) return $"Download & Use · {m.Groups[1].Value}";

        m = RxResumeDownloadPct().Match(s);
        if (m.Success) return $"Resume ({m.Groups[1].Value}%)";

        m = RxCancelDownloadPct().Match(s);
        if (m.Success) return $"Cancel ({m.Groups[1].Value}%)";

        m = RxOneClickDownloadMb().Match(s);
        if (m.Success) return $"Download ({m.Groups[1].Value})";

        m = RxIncludesBookFilesSize().Match(s);
        if (m.Success) return $"Includes book files · ~{m.Groups[1].Value}";

        m = RxDownloadedEpisodes().Match(s);
        if (m.Success) return $"Downloaded {m.Groups[1].Value} / {m.Groups[2].Value}";

        m = RxEpisodeDownloadingWait().Match(s);
        if (m.Success) return $"Downloading Ep. {m.Groups[1].Value} — playback will resume automatically";

        m = RxTrackSwitchedSeamless().Match(s);
        if (m.Success) return $"Switched track seamlessly: {T(m.Groups[1].Value)}";

        m = RxSkippedIntroSec().Match(s);
        if (m.Success) return $"Skipped intro ({m.Groups[1].Value}s)";

        m = RxUnreadSentencesCount().Match(s);
        if (m.Success) return $"Narrator skipped {m.Groups[1].Value} sentences";

        m = RxSavedPauseTime().Match(s);
        if (m.Success) return $"Saved {T(m.Groups[1].Value)} on this book";

        m = RxRecordingSkippedSentences().Match(s);
        if (m.Success) return $"Recording skipped {m.Groups[1].Value} sentences";

        m = RxAllChaptersReady().Match(s);
        if (m.Success) return $"All {m.Groups[1].Value} chapters ready";

        m = RxListenReadyChapters().Match(s);
        if (m.Success) return $"Listen to {m.Groups[1].Value} ready chapters";

        m = RxChaptersAlreadyReady().Match(s);
        if (m.Success) return $"{m.Groups[1].Value} chapters ready";

        m = RxChaptersCount().Match(s);
        if (m.Success) return m.Groups[1].Value == "1" ? "1 chapter" : $"{m.Groups[1].Value} chapters";

        m = RxHerePct().Match(s);
        if (m.Success) return $"Here {m.Groups[1].Value}%";

        m = RxChangelogItemsCount().Match(s);
        if (m.Success) return m.Groups[1].Value == "1" ? "1 item" : $"{m.Groups[1].Value} items";

        m = RxYuyueVer().Match(s);
        if (m.Success) return $"Yuyue {m.Groups[1].Value}";

        m = RxCheckedTodayAt().Match(s);
        if (m.Success) return $"Checked today at {m.Groups[1].Value}";

        m = RxCheckedMonthDayAt().Match(s);
        if (m.Success && int.TryParse(m.Groups[1].Value, out int cm) && cm >= 1 && cm <= 12)
        {
            string[] months = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
            return $"Checked {months[cm - 1]} {m.Groups[2].Value} at {m.Groups[3].Value}";
        }

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
            string autoName = Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(fe);
            if (!string.IsNullOrEmpty(autoName))
            {
                string origAuto = GetOrStoreOriginal(fe, "AutomationName", autoName);
                string nextAuto = T(origAuto);
                if (autoName != nextAuto) Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(fe, nextAuto);
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

        // Ensure every Button/ToggleButton whose Content is a Panel/Border/Icon exposes a non-empty UIA name
        // (WinUI 3 ButtonAutomationPeer returns "" when Content is a Grid/StackPanel rather than a string).
        if (node is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase btnBase &&
            string.IsNullOrEmpty(Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(btnBase)))
        {
            var parts = new List<string>(3);
            CollectReadableText(btnBase.Content, parts);
            string derived = parts.Count > 0
                ? string.Join(" ", parts)
                : (ToolTipService.GetToolTip(btnBase) as string ?? "");
            if (!string.IsNullOrWhiteSpace(derived))
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(btnBase, derived.Trim());
        }
    }

    private static void CollectReadableText(object? current, List<string> parts)
    {
        if (current is null || parts.Count >= 4) return;
        if (current is string s)
        {
            if (!string.IsNullOrWhiteSpace(s)) parts.Add(s.Trim());
            return;
        }
        if (current is TextBlock tb)
        {
            if (!string.IsNullOrWhiteSpace(tb.Text))
            {
                parts.Add(tb.Text.Trim());
            }
            else if (tb.Inlines.Count > 0)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var inl in tb.Inlines)
                {
                    if (inl is Run r && !string.IsNullOrEmpty(r.Text)) sb.Append(r.Text);
                }
                if (sb.Length > 0) parts.Add(sb.ToString().Trim());
            }
            return;
        }
        if (current is Panel panel)
        {
            foreach (var child in panel.Children)
            {
                CollectReadableText(child, parts);
                if (parts.Count >= 4) break;
            }
            return;
        }
        if (current is Border b)
        {
            CollectReadableText(b.Child, parts);
            return;
        }
        if (current is ContentPresenter cp)
        {
            CollectReadableText(cp.Content, parts);
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

        // Overlay windows-en.json directly so runtime never misses Windows-specific keys even if en.json wasn't rebuilt
        string[] winOverrideCandidates =
        [
            Path.Combine(AppContext.BaseDirectory, "Assets", "L10n", "windows-en.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "windows", "Yuyue", "Assets", "L10n", "windows-en.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "Assets", "L10n", "windows-en.json"),
        ];

        foreach (string path in winOverrideCandidates)
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

    [GeneratedRegex(@"^(\d{4})年(\d{1,2})月(\d{1,2})日$")]
    private static partial Regex RxFullDateYmd();

    [GeneratedRegex(@"^(\d{4})年(\d{1,2})月$")]
    private static partial Regex RxYearMonth();

    [GeneratedRegex(@"^(\d{1,2})\s*月\s*(\d{1,2})\s*日$")]
    private static partial Regex RxMonthDay();

    [GeneratedRegex(@"^(\d+)\s*条\s*·\s*来自\s*(\d+)\s*本书$")]
    private static partial Regex RxNotesSummary();

    [GeneratedRegex(@"^(\d+)\s*条$")]
    private static partial Regex RxItemsCount();

    [GeneratedRegex(@"^来自\s*(\d+)\s*本书$")]
    private static partial Regex RxFromBooksCount();

    [GeneratedRegex(@"^读到\s*第\s*(\d+)\s*章$")]
    private static partial Regex RxReadUpToChapter();

    [GeneratedRegex(@"^本章\s*(\d+)%$")]
    private static partial Regex RxChapterPct();

    [GeneratedRegex(@"^(在读|想读|读过|搁置|弃读)\s+(\d+)$")]
    private static partial Regex RxStatusChipCount();

    [GeneratedRegex(@"^(?:星期|周)([一二三四五日天])$")]
    private static partial Regex RxWeekday();

    [GeneratedRegex(@"^(\d+)\s*分钟前同步$")]
    private static partial Regex RxSyncMinutesAgo();

    [GeneratedRegex(@"^今天\s*(\d{1,2}:\d{2})\s*同步$")]
    private static partial Regex RxSyncTodayAt();

    [GeneratedRegex(@"^(\d{1,2}/\d{1,2}\s+\d{1,2}:\d{2})\s*同步$")]
    private static partial Regex RxSyncDateAt();

    [GeneratedRegex(@"^(.+?)\s*·\s*待同步$")]
    private static partial Regex RxPlacePendingSync();

    [GeneratedRegex(@"^(.+?)\s*·\s*刚刚同步$")]
    private static partial Regex RxPlaceJustSynced();

    [GeneratedRegex(@"^(\d+)\s*分钟前$")]
    private static partial Regex RxMinutesAgo();

    [GeneratedRegex(@"^(\d+)\s*小时前$")]
    private static partial Regex RxHoursAgo();

    [GeneratedRegex(@"^(\d+)\s*天前$")]
    private static partial Regex RxDaysAgo();

    [GeneratedRegex(@"^(\d+)\s*台设备$")]
    private static partial Regex RxDevicesCount();

    [GeneratedRegex(@"^留下\s*(\d+)\s*条$")]
    private static partial Regex RxKeptItems();

    [GeneratedRegex(@"^已放进合集[“""「](.+?)[”""」]$")]
    private static partial Regex RxAddedToCollection();

    [GeneratedRegex(@"^放进[「“""](.+?)[」”""]$")]
    private static partial Regex RxPutIntoCollection();

    [GeneratedRegex(@"^已导入\s*(\d+)\s*本$")]
    private static partial Regex RxImportedBooks();

    [GeneratedRegex(@"^共\s*(\d+)\s*本$")]
    private static partial Regex RxTotalBooks();

    [GeneratedRegex(@"^正在导入\s*(\d+)\s*/\s*(\d+)[…\.]{0,3}$")]
    private static partial Regex RxImportingProgress();

    [GeneratedRegex(@"^松开即可导入这\s*(\d+)\s*个文件$")]
    private static partial Regex RxDropToImportFiles();

    [GeneratedRegex(@"^读了\s*(\d+)\s*天$")]
    private static partial Regex RxReadDays();

    [GeneratedRegex(@"^听了\s*(\d+)\s*小时\s*(\d+)\s*分钟?$")]
    private static partial Regex RxListenedHoursMin();

    [GeneratedRegex(@"^听了\s*(\d+)\s*小时$")]
    private static partial Regex RxListenedHours();

    [GeneratedRegex(@"^听了\s*(\d+)\s*分钟?$")]
    private static partial Regex RxListenedMinutes();

    [GeneratedRegex(@"^第\s*(\d+)\s*/\s*(\d+)\s*章$")]
    private static partial Regex RxChapterOf();

    [GeneratedRegex(@"^第\s*(\d+)\s*章$")]
    private static partial Regex RxChapterSingle();

    [GeneratedRegex(@"^第\s*(\d+)\s*集$")]
    private static partial Regex RxEpisodeSingle();

    [GeneratedRegex(@"^约\s*(\d+)\s*小时\s*(\d+)\s*分钟?$")]
    private static partial Regex RxApproxHoursMin();

    [GeneratedRegex(@"^约\s*(\d+)\s*分钟?$")]
    private static partial Regex RxApproxMinutes();

    [GeneratedRegex(@"^(\d+)\s*小时\s*(\d+)\s*分钟?$")]
    private static partial Regex RxDurationHoursMin();

    [GeneratedRegex(@"^(\d+)\s*分钟?$")]
    private static partial Regex RxDurationMinutes();

    [GeneratedRegex(@"^全书还剩\s*(\d+)\s*小时\s*(\d+)\s*分钟?$")]
    private static partial Regex RxBookRemainingHoursMin();

    [GeneratedRegex(@"^全书还剩\s*(\d+)\s*分钟?$")]
    private static partial Regex RxBookRemainingMin();

    [GeneratedRegex(@"^还剩\s*(\d{1,2}:\d{2}(?::\d{2})?)$")]
    private static partial Regex RxRemainingClock();

    [GeneratedRegex(@"^定时还剩\s*(\d+:\d{2})$")]
    private static partial Regex RxSleepCountdown();

    [GeneratedRegex(@"^即将渐弱暂停\s*(\d+)s（按空格延\s*10\s*分）$")]
    private static partial Regex RxSleepFading();

    [GeneratedRegex(@"^(\d+)\s*字$")]
    private static partial Regex RxCharCount();

    [GeneratedRegex(@"^下载并选用\s*·\s*(.+)$")]
    private static partial Regex RxDownloadAndUseMb();

    [GeneratedRegex(@"^继续下载\s*\((\d+)%\)$")]
    private static partial Regex RxResumeDownloadPct();

    [GeneratedRegex(@"^取消下载\s*\((\d+)%\)$")]
    private static partial Regex RxCancelDownloadPct();

    [GeneratedRegex(@"^一键下载\s*\((.+?)\)$")]
    private static partial Regex RxOneClickDownloadMb();

    [GeneratedRegex(@"^含书文件\s*·\s*约\s*(.+)$")]
    private static partial Regex RxIncludesBookFilesSize();

    [GeneratedRegex(@"^已下\s*(\d+)\s*/\s*(\d+)\s*集$")]
    private static partial Regex RxDownloadedEpisodes();

    [GeneratedRegex(@"^第\s*(\d+)\s*集还在下，下好就接着放$")]
    private static partial Regex RxEpisodeDownloadingWait();

    [GeneratedRegex(@"^同句无缝切换：(.+)$")]
    private static partial Regex RxTrackSwitchedSeamless();

    [GeneratedRegex(@"^跳过了片头（(\d+)\s*秒）$")]
    private static partial Regex RxSkippedIntroSec();

    [GeneratedRegex(@"^这本有\s*(\d+)\s*句没念$")]
    private static partial Regex RxUnreadSentencesCount();

    [GeneratedRegex(@"^这本已经省了\s*(.+)$")]
    private static partial Regex RxSavedPauseTime();

    [GeneratedRegex(@"^录音里跳过了\s*(\d+)\s*句$")]
    private static partial Regex RxRecordingSkippedSentences();

    [GeneratedRegex(@"^(\d+)\s*章都准备好了$")]
    private static partial Regex RxAllChaptersReady();

    [GeneratedRegex(@"^先听对好的\s*(\d+)\s*章$")]
    private static partial Regex RxListenReadyChapters();

    [GeneratedRegex(@"^(\d+)\s*章已经能听$")]
    private static partial Regex RxChaptersAlreadyReady();

    [GeneratedRegex(@"^(\d+)\s*章$")]
    private static partial Regex RxChaptersCount();

    [GeneratedRegex(@"^这台在\s*(\d+)%$")]
    private static partial Regex RxHerePct();

    [GeneratedRegex(@"^(\d+)\s*项$")]
    private static partial Regex RxChangelogItemsCount();

    [GeneratedRegex(@"^语阅\s+(.+)$")]
    private static partial Regex RxYuyueVer();

    [GeneratedRegex(@"^今天\s*(\d{1,2}:\d{2})\s*检查过$")]
    private static partial Regex RxCheckedTodayAt();

    [GeneratedRegex(@"^(\d{1,2})\s*月\s*(\d{1,2})\s*日\s*(\d{1,2}:\d{2})\s*检查过$")]
    private static partial Regex RxCheckedMonthDayAt();
}
