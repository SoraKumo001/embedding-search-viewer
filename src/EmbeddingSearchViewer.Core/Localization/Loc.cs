using System.Collections;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Resources;

namespace EmbeddingSearchViewer.Core.Localization;

/// <summary>
/// The texts of the app. They live in Resources\Strings.resx (Japanese, the neutral language) and Strings.en.resx
/// (English); a text that is missing in a language falls back to the Japanese one.
///
/// The language is set once, when the app starts (<see cref="SetLanguage"/>), and a change in the settings takes effect
/// at the next start. Until it is set, the language is Japanese, whatever the language of Windows is: the unit tests,
/// which check the Japanese texts, do not depend on the machine they run on.
/// </summary>
public static class Loc
{
    public const string Auto = "auto";
    public const string Japanese = "ja";
    public const string English = "en";

    /// <summary>The languages that have texts, in the order the settings list them.</summary>
    public static readonly IReadOnlyList<string> Supported = new[] { Japanese, English };

    private static readonly ResourceManager Manager =
        new("EmbeddingSearchViewer.Core.Resources.Strings", typeof(Loc).Assembly);

    /// <summary>The texts of a language: the keys, and the properties of every uid.</summary>
    private sealed class LanguageTable
    {
        public LanguageTable(
            IReadOnlyDictionary<string, string> texts,
            IReadOnlyDictionary<string, IReadOnlyList<(string Property, string Value)>> uids)
        {
            Texts = texts;
            Uids = uids;
        }

        /// <summary>All the keys of the language, with the Japanese text where that language has none.</summary>
        public IReadOnlyDictionary<string, string> Texts { get; }

        /// <summary>
        /// The properties of every uid, so that the texts of one element are found without going through all the keys.
        /// </summary>
        public IReadOnlyDictionary<string, IReadOnlyList<(string Property, string Value)>> Uids { get; }
    }

    private static readonly ConcurrentDictionary<string, LanguageTable> Tables = new();

    private static CultureInfo _culture = CultureInfo.GetCultureInfo(Japanese);

    // A language that applies only to the code that runs inside UseLanguage (tests), so that tests running in parallel
    // do not change each other's language.
    private static readonly AsyncLocal<CultureInfo?> Scoped = new();

    /// <summary>The culture of the texts (and of the numbers and dates that are put into them).</summary>
    public static CultureInfo Culture => Scoped.Value ?? _culture;

    /// <summary>"ja" or "en".</summary>
    public static string Language => Culture.TwoLetterISOLanguageName;

    /// <summary>
    /// Uses another language until the returned object is disposed, for the code that runs from here on (including what it
    /// awaits), without changing the language of anything else. For tests and tools.
    /// </summary>
    public static IDisposable UseLanguage(string language)
    {
        var previous = Scoped.Value;
        Scoped.Value = CultureInfo.GetCultureInfo(Resolve(language, CultureInfo.InvariantCulture));
        return new Restore(() => Scoped.Value = previous);
    }

    private sealed class Restore(Action action) : IDisposable
    {
        public void Dispose() => action();
    }

    /// <summary>
    /// Chooses the language: "ja", "en", or "auto" (Japanese when Windows is set to Japanese, English otherwise).
    /// <paramref name="windowsCulture"/> is the language of Windows; the current UI culture when omitted.
    /// </summary>
    public static void SetLanguage(string? setting, CultureInfo? windowsCulture = null)
    {
        _culture = CultureInfo.GetCultureInfo(Resolve(setting, windowsCulture ?? CultureInfo.CurrentUICulture));
    }

    public static string Resolve(string? setting, CultureInfo windowsCulture) => setting switch
    {
        Japanese => Japanese,
        English => English,
        _ => windowsCulture.TwoLetterISOLanguageName == Japanese ? Japanese : English
    };

    /// <summary>The text for a key. A key that does not exist shows as "[key]", so that it is noticed.</summary>
    public static string T(string key) => Manager.GetString(key, Culture) ?? $"[{key}]";

    /// <summary>The text for a key, with the arguments put in ({0}, {1}, ...) using the culture of the language.</summary>
    public static string T(string key, params object?[] args) => string.Format(Culture, T(key), args);

    /// <summary>
    /// The properties of the element with the given uid, for the XAML attached property: every key "uid.Property" gives
    /// the value of that property (for example "Search.Title.Text").
    /// </summary>
    public static IReadOnlyList<(string Property, string Value)> ForUid(string uid)
    {
        if (uid.Length == 0) return Array.Empty<(string, string)>();

        var uids = Get(Culture).Uids;
        return uids.TryGetValue(uid, out var properties) ? properties : Array.Empty<(string, string)>();
    }

    /// <summary>All the keys of a language, with the Japanese text where that language has none.</summary>
    public static IReadOnlyDictionary<string, string> Table(CultureInfo culture) => Get(culture).Texts;

    /// <summary>
    /// The texts of a language, built once and kept: the language is set when the app starts and does not change while
    /// it runs, so the tables are built once per language. The key is the name of the culture, so that the language of
    /// <see cref="UseLanguage"/> (the tests) has a table of its own.
    /// </summary>
    private static LanguageTable Get(CultureInfo culture) =>
        Tables.GetOrAdd(culture.Name, _ => Build(culture));

    private static LanguageTable Build(CultureInfo culture)
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        AddAll(merged, Manager.GetResourceSet(CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: false));
        if (culture.TwoLetterISOLanguageName != Japanese)
        {
            AddAll(merged, Manager.GetResourceSet(culture, createIfNotExists: true, tryParents: false));
        }

        // The keys of every uid, so that the texts of one element are found by looking them up. A key "A.B.C" belongs
        // to the uid "A.B" and gives the property "C"; a key without a dot has no uid.
        var byUid = new Dictionary<string, List<(string Property, string Value)>>(StringComparer.Ordinal);
        foreach (var (key, value) in merged)
        {
            int dot = key.LastIndexOf('.');
            if (dot < 1) continue;
            string uid = key[..dot];
            if (!byUid.TryGetValue(uid, out var properties))
            {
                properties = new List<(string Property, string Value)>();
                byUid[uid] = properties;
            }
            properties.Add((key[(dot + 1)..], value));
        }

        var uids = new Dictionary<string, IReadOnlyList<(string Property, string Value)>>(StringComparer.Ordinal);
        foreach (var (uid, properties) in byUid)
        {
            uids[uid] = new ReadOnlyCollection<(string Property, string Value)>(properties);
        }

        return new LanguageTable(merged, uids);
    }

    private static void AddAll(Dictionary<string, string> into, ResourceSet? set)
    {
        if (set == null) return;
        foreach (DictionaryEntry entry in set)
        {
            if (entry.Key is string key && entry.Value is string value) into[key] = value;
        }
    }

    /// <summary>The keys of one language as they are stored (no fallback); used to check that the translations are complete.</summary>
    public static IReadOnlyDictionary<string, string> OwnTable(string language)
    {
        var own = new Dictionary<string, string>(StringComparer.Ordinal);
        var culture = language == Japanese ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo(language);
        AddAll(own, Manager.GetResourceSet(culture, createIfNotExists: true, tryParents: false));
        return own;
    }
}
