using System.Text.RegularExpressions;
using Yuyue.Design;

namespace Yuyue.Core;

/// <summary>
/// 纸色跟随外观: pairs light and dark papers with the app's effective appearance (浅色 / 深色 / 跟随系统)
/// and remembers the user's preferred paper for each side without overwriting synced day settings at night.
/// </summary>
public static class PaperAppearance
{
    public sealed record Resolved(Paper Preset, CustomPaper? Custom)
    {
        public string ChoiceId => Custom?.Id ?? PaperCssId(Preset);

        public bool IsDark
        {
            get
            {
                if (Custom is not null)
                {
                    try { return YuyueCoreMethods.PaperColors(Custom).Dark; }
                    catch { }
                }
                return YYPapers.FromId(PaperCssId(Preset)).IsDark;
            }
        }
    }

    public static bool IsAppDark(LibraryService library) => ThemeHelper.IsDark(library.Prefs.ToElementTheme());

    public static string PaperCssId(Paper p) => p switch
    {
        Paper.Dark => "dark",
        Paper.Kami => "kami",
        Paper.KamiNight => "kaminight",
        Paper.Sage => "sage",
        Paper.Eink => "eink",
        _ => "plain",
    };

    public static Paper? ParsePresetId(string? id) => id switch
    {
        "plain" => Paper.Plain,
        "dark" => Paper.Dark,
        "kami" => Paper.Kami,
        "kaminight" => Paper.KamiNight,
        "sage" => Paper.Sage,
        "eink" => Paper.Eink,
        _ => null,
    };

    public static Resolved Raw(LibraryService library, ReaderSettings settings)
    {
        if (!string.IsNullOrEmpty(settings.CustomPaper))
        {
            try
            {
                var mine = library.Core.CustomPapers().FirstOrDefault(cp => cp.Id == settings.CustomPaper);
                if (mine is not null)
                {
                    var preset = YuyueCoreMethods.PaperColors(mine).Preset;
                    return new Resolved(preset, mine);
                }
            }
            catch { }
        }
        return new Resolved(settings.Paper, null);
    }

    public static Resolved Resolve(LibraryService library, ReaderSettings settings, bool? appDarkOverride = null)
    {
        var basePaper = Raw(library, settings);
        if (!library.Prefs.PaperFollowAppearance) return basePaper;

        bool dark = appDarkOverride ?? IsAppDark(library);
        if (basePaper.IsDark == dark) return basePaper;

        string savedId = dark ? library.Prefs.PaperDarkId : library.Prefs.PaperLightId;
        if (!string.IsNullOrEmpty(savedId))
        {
            var saved = ResolveId(library, savedId);
            if (saved is not null && saved.IsDark == dark) return saved;
        }
        return new Resolved(PairedPreset(basePaper, dark), null);
    }

    private static Resolved? ResolveId(LibraryService library, string id)
    {
        if (ParsePresetId(id) is Paper preset) return new Resolved(preset, null);
        try
        {
            var mine = library.Core.CustomPapers().FirstOrDefault(cp => cp.Id == id);
            if (mine is not null)
            {
                return new Resolved(YuyueCoreMethods.PaperColors(mine).Preset, mine);
            }
        }
        catch { }
        return null;
    }

    public static Paper PairedPreset(Resolved basePaper, bool dark)
    {
        var layout = basePaper.Custom?.Layout ?? basePaper.Preset;
        return (layout, dark) switch
        {
            (Paper.Plain or Paper.Dark, false) => Paper.Plain,
            (Paper.Plain or Paper.Dark, true) => Paper.Dark,
            (_, false) => Paper.Kami,
            (_, true) => Paper.KamiNight,
        };
    }

    /// <summary>
    /// Picks a preset paper. Returns true if <paramref name="settings"/> should be saved to core
    /// (updating <paramref name="updated"/>), or false if only local night-slot prefs changed.
    /// </summary>
    public static bool PickPreset(LibraryService library, Paper preset, ReaderSettings settings, out ReaderSettings updated, bool? appDarkOverride = null, bool perBookOverride = false)
        => Pick(library, new Resolved(preset, null), settings, out updated, appDarkOverride, perBookOverride);

    /// <summary>
    /// Picks a custom paper (我的纸).
    /// </summary>
    public static bool PickCustom(LibraryService library, CustomPaper custom, ReaderSettings settings, out ReaderSettings updated, bool? appDarkOverride = null, bool perBookOverride = false)
    {
        var preset = YuyueCoreMethods.PaperColors(custom).Preset;
        return Pick(library, new Resolved(preset, custom), settings, out updated, appDarkOverride, perBookOverride);
    }

    private static bool Pick(LibraryService library, Resolved chosen, ReaderSettings settings, out ReaderSettings updated, bool? appDarkOverride = null, bool perBookOverride = false)
    {
        if (perBookOverride)
        {
            updated = settings with { Paper = chosen.Preset, CustomPaper = chosen.Custom?.Id };
            return true;
        }
        updated = settings;
        var basePaper = Raw(library, settings);
        bool dark = appDarkOverride ?? IsAppDark(library);
        Remember(library, chosen.ChoiceId, chosen.IsDark);

        if (library.Prefs.PaperFollowAppearance)
        {
            if (chosen.IsDark == dark)
            {
                if (basePaper.IsDark == chosen.IsDark)
                {
                    updated = settings with { Paper = chosen.Preset, CustomPaper = chosen.Custom?.Id };
                    library.SavePrefs();
                    return true;
                }
                if (!dark)
                {
                    if (string.IsNullOrEmpty(library.Prefs.PaperDarkId))
                        Remember(library, basePaper.ChoiceId, true);
                    updated = settings with { Paper = chosen.Preset, CustomPaper = chosen.Custom?.Id };
                    library.SavePrefs();
                    return true;
                }
                library.SavePrefs();
                return false;
            }

            if (basePaper.IsDark == dark)
                Remember(library, basePaper.ChoiceId, dark);
            library.Prefs.PaperFollowAppearance = false;
            library.SavePrefs();
            updated = settings with { Paper = chosen.Preset, CustomPaper = chosen.Custom?.Id };
            return true;
        }

        library.SavePrefs();
        updated = settings with { Paper = chosen.Preset, CustomPaper = chosen.Custom?.Id };
        return true;
    }

    /// <summary>
    /// Turns 跟随外观 on or off. Turning off freezes the currently visible paper so the screen never flashes.
    /// Returns true if <paramref name="updated"/> differs from <paramref name="settings"/> and should be saved.
    /// </summary>
    public static bool SetFollow(LibraryService library, bool on, ReaderSettings settings, out ReaderSettings updated, bool? appDarkOverride = null)
    {
        updated = settings;
        if (on == library.Prefs.PaperFollowAppearance) return false;

        if (!on)
        {
            var visible = Resolve(library, settings, appDarkOverride);
            var basePaper = Raw(library, settings);
            if (basePaper.IsDark != visible.IsDark)
                Remember(library, basePaper.ChoiceId, basePaper.IsDark);
            library.Prefs.PaperFollowAppearance = false;
            library.SavePrefs();
            if (visible.Preset != settings.Paper || visible.Custom?.Id != settings.CustomPaper)
            {
                updated = settings with { Paper = visible.Preset, CustomPaper = visible.Custom?.Id };
                return true;
            }
            return false;
        }

        library.Prefs.PaperFollowAppearance = true;
        library.SavePrefs();
        return false;
    }

    public static void Forget(LibraryService library, string id)
    {
        bool changed = false;
        if (library.Prefs.PaperLightId == id)
        {
            library.Prefs.PaperLightId = "";
            changed = true;
        }
        if (library.Prefs.PaperDarkId == id)
        {
            library.Prefs.PaperDarkId = "";
            changed = true;
        }
        if (changed) library.SavePrefs();
    }

    private static void Remember(LibraryService library, string id, bool dark)
    {
        if (dark) library.Prefs.PaperDarkId = id;
        else library.Prefs.PaperLightId = id;
    }

    /// <summary>
    /// Rewrites the opening &lt;html ...&gt; attributes before WebView2 paints its first frame so opening a book at night never flashes white.
    /// </summary>
    public static string ApplyToHtml(string body, Resolved resolved)
    {
        string? customStyle = null;
        string? customTexture = null;
        if (resolved.Custom is not null)
        {
            try
            {
                customStyle = YuyueCoreMethods.PaperStyle(resolved.Custom);
                customTexture = resolved.Custom.Texture == PaperTexture.None
                    ? null
                    : resolved.Custom.Texture.ToString().ToLowerInvariant();
            }
            catch { }
        }
        return ApplyToHtml(body, PaperCssId(resolved.Preset), customStyle, customTexture);
    }

    public static string ApplyToHtml(string body, string presetId, string? customStyle, string? customTexture)
    {
        int start = body.IndexOf("<html ", StringComparison.Ordinal);
        if (start < 0) return body;
        int end = body.IndexOf('>', start);
        if (end < 0) return body;

        string tag = body[start..end];
        if (!tag.Contains("data-yy-paper=", StringComparison.Ordinal)) return body;

        tag = Regex.Replace(tag, @"data-yy-paper=""[^""]*""", $@"data-yy-paper=""{presetId}""");
        tag = Regex.Replace(tag, @" data-yy-texture=""[^""]*""", "");
        if (!string.IsNullOrEmpty(customTexture))
        {
            int styleIdx = tag.IndexOf(" style=\"", StringComparison.Ordinal);
            if (styleIdx >= 0)
                tag = tag[..styleIdx] + $@" data-yy-texture=""{customTexture}""" + tag[styleIdx..];
        }

        var styleMatch = Regex.Match(tag, @"style=""[^""]*""");
        if (styleMatch.Success)
        {
            string styleAttr = styleMatch.Value;
            int customIdx = styleAttr.IndexOf(";--yy-p-bg:", StringComparison.Ordinal);
            if (customIdx >= 0)
                styleAttr = styleAttr[..customIdx] + "\"";
            if (!string.IsNullOrEmpty(customStyle))
                styleAttr = styleAttr[..^1] + ";" + customStyle + "\"";
            tag = tag[..styleMatch.Index] + styleAttr + tag[(styleMatch.Index + styleMatch.Length)..];
        }

        return body[..start] + tag + body[end..];
    }
}
