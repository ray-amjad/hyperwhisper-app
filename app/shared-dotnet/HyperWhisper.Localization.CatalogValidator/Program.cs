using System.Globalization;
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
    }

    Console.WriteLine($"Validated {paths.Length} localization catalogs with {baseCatalog.Count} keys each.");
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
