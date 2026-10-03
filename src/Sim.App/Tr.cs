using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sim.App;

/// <summary>
/// UI language (M10). Text is written in English everywhere; in Chinese, <see cref="Ui"/> passes every
/// string it draws or measures through <see cref="T"/>, which looks it up in <c>config/strings.zh.json</c>.
/// Entries are English to Chinese; <c>{0}</c>, <c>{1}</c>, ... stand for numbers or names filled in at
/// run time ("best {0}" also matches "best 87"). Lines made of parts separated by three spaces are
/// translated part by part. Anything without an entry stays English. The tuning panel is a developer
/// tool and stays mostly English.
/// </summary>
public static class Tr
{
    public const string Separator = "   ";
    private const int CacheLimit = 4096;

    private static Dictionary<string, string> _exact = [];
    private static (Regex Pattern, string Prefix, string Zh)[] _patterns = [];
    private static readonly Dictionary<string, string> Cache = [];

    /// <summary>"en" or "zh".</summary>
    public static string Language { get; private set; } = "en";

    public static bool Chinese => Language == "zh";

    /// <summary>Every Chinese string in the table (the CJK font only needs these glyphs).</summary>
    public static IEnumerable<string> ChineseTexts => _exact.Values.Concat(_patterns.Select(p => p.Zh));

    /// <summary>Loads the table from the JSON text of <c>strings.zh.json</c> (an object of English: Chinese).</summary>
    public static void LoadTable(string json)
    {
        var map = JsonSerializer.Deserialize<Dictionary<string, string>>(json, new JsonSerializerOptions
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        }) ?? [];
        _exact = map.Where(kv => !kv.Key.Contains('{')).ToDictionary(kv => kv.Key, kv => kv.Value);
        _patterns = map.Where(kv => kv.Key.Contains('{'))
            .Select(kv => (Compile(kv.Key), kv.Key[..kv.Key.IndexOf('{')], kv.Value))
            // Longer, more specific patterns first.
            .OrderByDescending(p => p.Item1.ToString().Length).ToArray();
        Cache.Clear();
    }

    public static void SetLanguage(string language)
    {
        Language = language == "zh" ? "zh" : "en";
        Cache.Clear();
    }

    /// <summary>The text in the current language.</summary>
    public static string T(string text)
    {
        if (!Chinese || text.Length == 0) return text;
        if (Cache.TryGetValue(text, out var hit)) return hit;
        string result = Whole(text) ?? (text.Contains(Separator)
            ? string.Join(Separator, text.Split(Separator).Select(part => Whole(part.Trim()) is { } zh ? zh : part))
            : text);
        if (Cache.Count >= CacheLimit) Cache.Clear();
        Cache[text] = result;
        return result;
    }

    private static string? Whole(string text)
    {
        if (_exact.TryGetValue(text, out var zh)) return zh;
        foreach (var (pattern, prefix, target) in _patterns)
        {
            if (!text.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var m = pattern.Match(text);
            if (!m.Success) continue;
            string filled = target;
            for (int i = 1; i < m.Groups.Count; i++)
            {
                string value = m.Groups[i].Value;
                // A filled-in part may itself be translatable (a failure reason, a metric name).
                filled = filled.Replace("{" + (i - 1) + "}", Whole(value) ?? value);
            }
            return filled;
        }
        return null;
    }

    /// <summary>"best {0} of {1}" becomes ^best (.+?) of (.+?)$.</summary>
    private static Regex Compile(string english)
    {
        var parts = Regex.Split(english, @"\{\d+\}");
        return new Regex("^" + string.Join("(.+?)", parts.Select(Regex.Escape)) + "$", RegexOptions.CultureInvariant);
    }
}
