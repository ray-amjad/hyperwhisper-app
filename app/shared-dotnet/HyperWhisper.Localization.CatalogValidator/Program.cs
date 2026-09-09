using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

if (args.Length != 1 || string.IsNullOrWhiteSpace(args[0]))
{
    Console.Error.WriteLine("Usage: localization-catalog-validator <resource-directory>");
    return 2;
}

var directory = Path.GetFullPath(args[0]);
try
{
    var paths = Directory.GetFiles(directory, "Strings*.resx").Order(StringComparer.Ordinal).ToArray();
    if (paths.Length != 40)
    {
        throw new InvalidDataException(
            $"Expected 40 localization catalogs, found {paths.Length} in '{directory}'.");
    }

    var basePath = Path.Combine(directory, "Strings.resx");
    if (!File.Exists(basePath))
    {
        throw new InvalidDataException("The invariant Strings.resx catalog is missing.");
    }

    var baseCatalog = ReadCatalog(basePath);

    // Issue #552. Key parity above proves every catalog has every KEY; it says
    // nothing about the VALUES, and 36 of the 39 held raw English for about half
    // of them. `translation-status.json` records the two halves of that: the
    // keys that are supposed to stay English, and a per-locale ceiling on the
    // ones that are not. See the $schema-note inside the file.
    var status = ReadTranslationStatus(Path.Combine(directory, "translation-status.json"), baseCatalog);
    var untranslatedCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);

    // Issue #574. The ceiling above only sees a value that IS the English one.
    // The neighbouring defect is a value that is present, not English, and wrong
    // — and the way it happens in bulk is that a base value is rewritten and the
    // 39 catalogs keep a translation of the superseded English. Nothing about
    // one such value looks wrong on its own, but the AGREEMENT does: twenty
    // independent translators do not produce the same string by accident, so a
    // value shared by more locales than the threshold is leftover English.
    var localesSharingValue = new Dictionary<(string Key, string Value), List<string>>();

    foreach (var path in paths)
    {
        var catalog = ReadCatalog(path);
        var missing = baseCatalog.Keys.Except(catalog.Keys, StringComparer.Ordinal).Order().ToArray();
        var extra = catalog.Keys.Except(baseCatalog.Keys, StringComparer.Ordinal).Order().ToArray();
        if (missing.Length != 0 || extra.Length != 0)
        {
            throw new InvalidDataException(
                $"{Path.GetFileName(path)} keys differ from the base catalog " +
                $"(missing: {string.Join(", ", missing)}; extra: {string.Join(", ", extra)}).");
        }

        foreach (var key in baseCatalog.Keys)
        {
            var expected = PlaceholderIndexes(baseCatalog[key]);
            var actual = PlaceholderIndexes(catalog[key]);
            if (!expected.SequenceEqual(actual))
            {
                throw new InvalidDataException(
                    $"{Path.GetFileName(path)} key '{key}' has placeholders " +
                    $"[{string.Join(",", actual)}], expected [{string.Join(",", expected)}].");
            }
        }

        if (!string.Equals(path, basePath, StringComparison.Ordinal))
        {
            var locale = LocaleOf(path);
            untranslatedCounts[locale] = baseCatalog.Count(
                pair => IsUntranslated(pair.Key, pair.Value, catalog[pair.Key], status.IdenticalByDesign));

            foreach (var (key, english) in baseCatalog)
            {
                var localized = catalog[key];
                // A value that IS the English one is the ceiling gate's business,
                // and a value with no letters is the same string everywhere.
                if (!HasLetters(localized) || string.Equals(localized, english, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!localesSharingValue.TryGetValue((key, localized), out var sharers))
                {
                    sharers = [];
                    localesSharingValue[(key, localized)] = sharers;
                }

                sharers.Add(locale);
            }
        }
    }

    CheckCeilings(untranslatedCounts, status.Ceilings);
    CheckSharedValues(localesSharingValue, status.SharedValueCeiling, baseCatalog);

    var translatable = baseCatalog.Count(
        pair => !status.IdenticalByDesign.Contains(pair.Key) && HasLetters(pair.Value));
    Console.WriteLine(
        $"Validated {paths.Length} localization catalogs with {baseCatalog.Count} keys each " +
        $"({translatable} translatable, {status.IdenticalByDesign.Count} identical by design; " +
        $"{untranslatedCounts.Values.Sum()} values still untranslated across {untranslatedCounts.Count} locales).");
    return 0;
}
catch (Exception exception) when (exception is IOException or InvalidDataException or System.Xml.XmlException)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

static Dictionary<string, string> ReadCatalog(string path)
{
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var data in XDocument.Load(path).Root?.Elements("data") ?? [])
    {
        var key = (string?)data.Attribute("name");
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} contains a resource without a key.");
        }

        if (!result.TryAdd(key, data.Element("value")?.Value ?? string.Empty))
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} contains duplicate key '{key}'.");
        }
    }

    return result;
}

static string LocaleOf(string path)
{
    // Strings.zh-Hant.resx -> zh-Hant
    var name = Path.GetFileNameWithoutExtension(path);
    return name["Strings.".Length..];
}

// A value the user could tell apart from English. "{0}" and "127.0.0.1:{0}" are
// the same string in every language, so they are exempt by rule and need no
// entry in translation-status.json.
static bool HasLetters(string value) => value.Any(char.IsLetter);

static bool IsUntranslated(string key, string english, string localized, IReadOnlySet<string> byDesign) =>
    !byDesign.Contains(key)
    && HasLetters(english)
    && string.Equals(localized, english, StringComparison.Ordinal);

static (IReadOnlySet<string> IdenticalByDesign, IReadOnlyDictionary<string, int> Ceilings, int SharedValueCeiling)
    ReadTranslationStatus(
    string path,
    Dictionary<string, string> baseCatalog)
{
    if (!File.Exists(path))
    {
        throw new InvalidDataException(
            $"'{Path.GetFileName(path)}' is missing. It records which keys are identical to English " +
            "by design and the per-locale ceiling on the ones that are not (issue #552).");
    }

    JsonElement root;
    try
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        root = document.RootElement.Clone();
    }
    catch (JsonException exception)
    {
        throw new InvalidDataException($"{Path.GetFileName(path)} is not valid JSON: {exception.Message}");
    }

    if (!root.TryGetProperty("identicalByDesign", out var byDesignElement)
        || byDesignElement.ValueKind != JsonValueKind.Object)
    {
        throw new InvalidDataException($"{Path.GetFileName(path)} needs an 'identicalByDesign' object.");
    }

    if (!root.TryGetProperty("ceilings", out var ceilingsElement)
        || ceilingsElement.ValueKind != JsonValueKind.Object)
    {
        throw new InvalidDataException($"{Path.GetFileName(path)} needs a 'ceilings' object.");
    }

    var byDesign = new HashSet<string>(StringComparer.Ordinal);
    foreach (var property in byDesignElement.EnumerateObject())
    {
        // A stale entry would silently excuse a key from translation forever,
        // so a rename has to break the build rather than quietly shrink the set.
        if (!baseCatalog.ContainsKey(property.Name))
        {
            throw new InvalidDataException(
                $"{Path.GetFileName(path)} lists '{property.Name}' as identical by design, " +
                "but that key is not in Strings.resx. Remove or rename the entry.");
        }

        if (string.IsNullOrWhiteSpace(property.Value.GetString()))
        {
            throw new InvalidDataException(
                $"{Path.GetFileName(path)} entry '{property.Name}' needs a reason, so the list stays reviewable.");
        }

        byDesign.Add(property.Name);
    }

    var ceilings = new Dictionary<string, int>(StringComparer.Ordinal);
    foreach (var property in ceilingsElement.EnumerateObject())
    {
        if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var ceiling) || ceiling < 0)
        {
            throw new InvalidDataException(
                $"{Path.GetFileName(path)} ceiling for '{property.Name}' must be a non-negative integer.");
        }

        ceilings[property.Name] = ceiling;
    }

    if (!root.TryGetProperty("sharedValueCeiling", out var sharedElement)
        || sharedElement.ValueKind != JsonValueKind.Number
        || !sharedElement.TryGetInt32(out var sharedValueCeiling)
        || sharedValueCeiling < 1)
    {
        throw new InvalidDataException(
            $"{Path.GetFileName(path)} needs a positive integer 'sharedValueCeiling' (issue #574).");
    }

    return (byDesign, ceilings, sharedValueCeiling);
}

// Issue #574. See the note at the call site: agreement between many locales on a
// value that is not the English one is the signature of stale English, not of
// translation. The threshold has real headroom — the largest honest coincidence
// in the catalogs is "Mikrofon", which 13 languages share.
static void CheckSharedValues(
    IReadOnlyDictionary<(string Key, string Value), List<string>> localesSharingValue,
    int ceiling,
    IReadOnlyDictionary<string, string> baseCatalog)
{
    var over = localesSharingValue
        .Where(pair => pair.Value.Count > ceiling)
        .OrderByDescending(pair => pair.Value.Count)
        .ThenBy(pair => pair.Key.Key, StringComparer.Ordinal)
        .Select(pair =>
            $"'{pair.Key.Key}' is \"{pair.Key.Value}\" in {pair.Value.Count} locales " +
            $"while Strings.resx says \"{baseCatalog[pair.Key.Key]}\"")
        .ToArray();
    if (over.Length != 0)
    {
        throw new InvalidDataException(
            $"{over.Length} value(s) are shared by more than {ceiling} locales: {string.Join("; ", over)}. " +
            "That is what a rewritten base value looks like: the English changed and the catalogs kept a " +
            "translation of the old text. Retranslate them, do not raise the ceiling (issue #574).");
    }
}

static void CheckCeilings(
    IReadOnlyDictionary<string, int> counts,
    IReadOnlyDictionary<string, int> ceilings)
{
    var unknown = ceilings.Keys.Except(counts.Keys, StringComparer.Ordinal).Order().ToArray();
    if (unknown.Length != 0)
    {
        throw new InvalidDataException(
            $"translation-status.json has ceilings for locales with no catalog: {string.Join(", ", unknown)}.");
    }

    var uncovered = counts.Keys.Except(ceilings.Keys, StringComparer.Ordinal).Order().ToArray();
    if (uncovered.Length != 0)
    {
        throw new InvalidDataException(
            $"translation-status.json has no ceiling for: {string.Join(", ", uncovered)}. " +
            "Reseed with untranslated_resx.py --seed.");
    }

    // Only ever fails UPWARD. A locale that comes in under its ceiling is a
    // translation batch that has not reseeded yet, which is fine and is only
    // reported — making that an error would turn this file into the same
    // cross-PR conflict magnet the base key count already is.
    var over = counts
        .Where(pair => pair.Value > ceilings[pair.Key])
        .Select(pair => $"{pair.Key} {pair.Value} > {ceilings[pair.Key]}")
        .Order(StringComparer.Ordinal)
        .ToArray();
    if (over.Length != 0)
    {
        throw new InvalidDataException(
            $"{over.Length} locale(s) gained untranslated values: {string.Join("; ", over)}. " +
            "A new key in Strings.resx must be translated in every catalog, not copied through as English. " +
            "See translation-status.json (issue #552).");
    }

    var slack = counts.Where(pair => pair.Value < ceilings[pair.Key]).ToArray();
    if (slack.Length != 0)
    {
        Console.WriteLine(
            $"{slack.Length} locale(s) are now below their recorded ceiling by " +
            $"{slack.Sum(pair => ceilings[pair.Key] - pair.Value)} values in total. " +
            "Run untranslated_resx.py --seed to lower the ceilings.");
    }
}

static int[] PlaceholderIndexes(string value)
{
    var legacyIndex = 0;
    var normalized = Regex.Replace(value, @"%d", _ => $"{{{legacyIndex++}}}", RegexOptions.CultureInvariant);
    return Regex.Matches(
            normalized,
            @"(?<!\{)\{(?<index>\d+)(?:,[^}:]+)?(?::[^}]*)?\}(?!\})",
            RegexOptions.CultureInvariant)
        .Select(match => int.Parse(match.Groups["index"].Value, CultureInfo.InvariantCulture))
        .Order()
        .ToArray();
}
