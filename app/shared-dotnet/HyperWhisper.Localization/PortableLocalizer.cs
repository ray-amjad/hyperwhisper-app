using System.Collections.ObjectModel;
using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;

namespace HyperWhisper.Localization;

public readonly record struct LocalizationKey
{
    internal LocalizationKey(string value) => Value = value;

    public string Value { get; }

    public override string ToString() => Value;
}

public enum LocalizationIdentifierKind
{
    Provider,
    Model,
    PersistedValue,
}

public sealed class LocalizationFormatException(string message) : FormatException(message);

public sealed class PortableLocalizer
{
    private const string BaseName = "HyperWhisper.Localization.Resources.Strings";
    private static readonly ResourceManager Resources = new(BaseName, typeof(PortableLocalizer).Assembly);
    private static readonly Regex DotNetPlaceholder = new(
        @"(?<!\{)\{(?<index>\d+)(?:,[^}:]+)?(?::[^}]*)?\}(?!\})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex LegacyIntegerPlaceholder = new(
        @"%d",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly string[] CultureNames =
    [
        "ar", "bg", "ca", "cs", "da", "de", "el", "es", "et", "fi",
        "fr", "he", "hi", "hr", "hu", "id", "is", "it", "ja", "ko",
        "lt", "lv", "ms", "nb", "nl", "pl", "pt", "ro", "ru", "sk",
        "sl", "sr", "sv", "th", "tr", "uk", "vi", "zh-Hans", "zh-Hant",
    ];
    private static readonly IReadOnlySet<string> BaseKeys = LoadBaseKeys();

    public static IReadOnlyList<CultureInfo> SupportedCultures { get; } =
        new ReadOnlyCollection<CultureInfo>(CultureNames.Select(CultureInfo.GetCultureInfo).ToArray());

    public static int BaseKeyCount => BaseKeys.Count;

    public CultureInfo Culture { get; }

    public bool IsRightToLeft => Culture.TextInfo.IsRightToLeft;

    public PortableLocalizer(CultureInfo? culture = null)
    {
        Culture = CultureInfo.ReadOnly(culture ?? CultureInfo.CurrentUICulture);
    }

    public LocalizationKey Key(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (!BaseKeys.Contains(key))
        {
            throw new KeyNotFoundException($"Unknown localization key '{key}'.");
        }

        return new LocalizationKey(key);
    }

    public string Get(LocalizationKey key)
    {
        if (!BaseKeys.Contains(key.Value))
        {
            throw new KeyNotFoundException($"Unknown localization key '{key.Value}'.");
        }

        return Get(key.Value);
    }

    public string Get(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return string.Empty;
        }

        return Resources.GetString(key, Culture)
            ?? Resources.GetString(key, CultureInfo.InvariantCulture)
            ?? key;
    }

    public string Format(LocalizationKey key, params object?[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var baseFormat = Resources.GetString(key.Value, CultureInfo.InvariantCulture)
            ?? throw new KeyNotFoundException($"Unknown localization key '{key.Value}'.");
        var localizedFormat = NormalizeLegacyPlaceholders(Get(key));
        var expected = PlaceholderIndexes(baseFormat);
        var actual = PlaceholderIndexes(localizedFormat);
        if (!expected.SequenceEqual(actual))
        {
            throw new LocalizationFormatException(
                $"Localization key '{key.Value}' has placeholders that do not match the base catalog.");
        }

        var requiredArgumentCount = expected.Count == 0 ? 0 : expected[^1] + 1;
        if (arguments.Length != requiredArgumentCount)
        {
            throw new LocalizationFormatException(
                $"Localization key '{key.Value}' requires {requiredArgumentCount} formatting argument(s), but {arguments.Length} were supplied.");
        }

        try
        {
            return string.Format(Culture, localizedFormat, arguments);
        }
        catch (FormatException exception)
        {
            throw new LocalizationFormatException(
                $"Localization key '{key.Value}' contains an invalid composite format: {exception.Message}");
        }
    }

    public static string PreserveIdentifier(LocalizationIdentifierKind kind, string identifier)
    {
        _ = kind;
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        return identifier;
    }

    internal static IReadOnlyList<int> PlaceholderIndexes(string value)
    {
        var normalized = NormalizeLegacyPlaceholders(value);
        return DotNetPlaceholder.Matches(normalized)
            .Select(match => int.Parse(match.Groups["index"].Value, CultureInfo.InvariantCulture))
            .Order()
            .ToArray();
    }

    internal static string NormalizeLegacyPlaceholders(string value)
    {
        var nextIndex = 0;
        return LegacyIntegerPlaceholder.Replace(value, _ => $"{{{nextIndex++}}}");
    }

    private static IReadOnlySet<string> LoadBaseKeys()
    {
        var resourceSet = Resources.GetResourceSet(CultureInfo.InvariantCulture, true, false)
            ?? throw new MissingManifestResourceException("The base HyperWhisper localization catalog is missing.");
        return resourceSet.Cast<System.Collections.DictionaryEntry>()
            .Select(entry => (string)entry.Key)
            .ToHashSet(StringComparer.Ordinal);
    }
}
