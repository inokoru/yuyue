using System.Text.RegularExpressions;

namespace Yuyue.Core;

public sealed record BuiltInWordHit(string Headword, string? Phonetic, string Meaning);

/// <summary>
/// Offline built-in English-Chinese dictionary backed by <c>Assets/Dict/ecdict.tsv</c>
/// (ECDICT, MIT License, ~90K entries). Lazy-loaded once on first lookup (~40 ms).
/// </summary>
public sealed class BuiltInEcdict
{
    private readonly object _gate = new();
    private Dictionary<string, (string Phonetic, string Meaning)>? _entries;
    private Dictionary<string, string>? _lemmas;
    private bool _loaded;

    public string FilePath { get; }
    public bool IsAvailable => File.Exists(FilePath);

    public BuiltInEcdict()
    {
        FilePath = Path.Combine(AppContext.BaseDirectory, "Assets", "Dict", "ecdict.tsv");
    }

    public void PrewarmAsync()
    {
        if (_loaded) return;
        _ = Task.Run(EnsureLoaded);
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        lock (_gate)
        {
            if (_loaded) return;
            _loaded = true;
            if (!File.Exists(FilePath)) return;

            var entries = new Dictionary<string, (string, string)>(64000, StringComparer.OrdinalIgnoreCase);
            var lemmas = new Dictionary<string, string>(32000, StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var line in File.ReadLines(FilePath))
                {
                    if (string.IsNullOrWhiteSpace(line) || line[0] == '#') continue;
                    int tab1 = line.IndexOf('\t');
                    if (tab1 <= 0) continue;
                    string word = line[..tab1];
                    string rest = line[(tab1 + 1)..];
                    if (rest.StartsWith('='))
                    {
                        lemmas[word] = rest[1..].Trim();
                        continue;
                    }
                    int tab2 = rest.IndexOf('\t');
                    if (tab2 < 0) continue;
                    string phonetic = rest[..tab2].Trim();
                    string meaning = rest[(tab2 + 1)..]
                        .Replace("\\r\\n", "\n")
                        .Replace("\\n", "\n")
                        .Trim();
                    entries[word] = (phonetic, meaning);
                }
            }
            catch { }

            _entries = entries;
            _lemmas = lemmas;
        }
    }

    public BuiltInWordHit? LookUp(string raw)
    {
        EnsureLoaded();
        if (_entries is null || string.IsNullOrWhiteSpace(raw)) return null;

        string cleaned = Regex.Replace(raw.Trim(), @"^[^A-Za-z]+|[^A-Za-z]+$", "").ToLowerInvariant();
        if (string.IsNullOrEmpty(cleaned)) return null;

        foreach (var cand in CandidateForms(cleaned))
        {
            var hit = DirectLookUp(cand);
            if (hit is not null) return hit;
        }
        return null;
    }

    private BuiltInWordHit? DirectLookUp(string word)
    {
        if (_entries is null) return null;
        if (_entries.TryGetValue(word, out var direct))
            return new BuiltInWordHit(word, string.IsNullOrEmpty(direct.Phonetic) ? null : direct.Phonetic, direct.Meaning);

        if (_lemmas is not null && _lemmas.TryGetValue(word, out var lemma) && _entries.TryGetValue(lemma, out var target))
            return new BuiltInWordHit(lemma, string.IsNullOrEmpty(target.Phonetic) ? null : target.Phonetic, target.Meaning);

        return null;
    }

    private static IEnumerable<string> CandidateForms(string w)
    {
        yield return w;
        if (w.EndsWith("'s")) yield return w[..^2];
        if (w.EndsWith("ies") && w.Length > 4) yield return w[..^3] + "y";
        if (w.EndsWith("ied") && w.Length > 4) yield return w[..^3] + "y";
        if (w.EndsWith("es") && w.Length > 3) yield return w[..^2];
        if (w.EndsWith("s") && w.Length > 2) yield return w[..^1];
        if (w.EndsWith("ing") && w.Length > 5)
        {
            yield return w[..^3];
            yield return w[..^3] + "e";
            if (w.Length > 6 && w[^4] == w[^5]) yield return w[..^4];
        }
        if (w.EndsWith("ed") && w.Length > 4)
        {
            yield return w[..^2];
            yield return w[..^1];
            if (w.Length > 5 && w[^3] == w[^4]) yield return w[..^3];
        }
        if (w.EndsWith("ly") && w.Length > 4) yield return w[..^2];
    }
}
