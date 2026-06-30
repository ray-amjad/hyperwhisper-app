namespace HyperWhisper.Localization;

/// <summary>
/// Static localization accessor that mirrors the macOS .localized pattern.
/// Provides a simple API for retrieving localized strings from resources.
///
/// Usage:
///   C#: Loc.S("key") or Loc.S("key", arg1, arg2)
///   XAML: {loc:Loc key}
/// </summary>
public static class Loc
{
    /// <summary>
    /// Gets a localized string for the specified key.
    /// Returns the key itself if the string is not found.
    /// </summary>
    /// <param name="key">The resource key (e.g., "home.welcome.title")</param>
    /// <returns>The localized string or the key if not found</returns>
    public static string S(string key)
    {
        if (string.IsNullOrEmpty(key))
            return string.Empty;

        return Resources.Strings.ResourceManager.GetString(key,
            Resources.Strings.Culture) ?? key;
    }

    /// <summary>
    /// Gets a localized string and formats it with the provided arguments.
    /// Uses string.Format internally, so placeholders should be {0}, {1}, etc.
    /// </summary>
    /// <param name="key">The resource key (e.g., "errors.fileSize" with value "File {0} is too large")</param>
    /// <param name="args">Arguments to format into the string</param>
    /// <returns>The formatted localized string</returns>
    public static string S(string key, params object[] args)
    {
        var format = S(key);
        if (args == null || args.Length == 0)
            return format;

        try
        {
            return string.Format(format, args);
        }
        catch (FormatException)
        {
            // If formatting fails, return the unformatted string
            return format;
        }
    }
}
