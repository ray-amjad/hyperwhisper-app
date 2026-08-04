namespace HyperWhisper.Services;

/// <summary>
/// Language codes xAI's Grok API (batch STT and streaming) supports language-driven
/// formatting for. Shared by <see cref="GrokSttService"/> (batch) and
/// <see cref="Streaming.XaiStreamingStrategy"/> (streaming) so the two providers can't drift.
/// </summary>
internal static class XaiFormattingLanguages
{
    private static readonly HashSet<string> SupportedCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "ar", "cs", "da", "de", "en", "es", "fa", "fil", "fr", "hi",
        "id", "it", "ja", "ko", "mk", "ms", "nl", "pl", "pt", "ro",
        "ru", "sv", "th", "tr", "vi"
    };

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        { "tl", "fil" }
    };

    /// <summary>
    /// Normalizes <paramref name="code"/> (trim, take the primary subtag before any
    /// '-', lowercase, resolve aliases) and reports whether the result is one xAI
    /// supports language-driven formatting for.
    /// </summary>
    public static bool TryGetSupportedCode(string? code, out string supportedCode)
    {
        supportedCode = string.Empty;

        if (string.IsNullOrWhiteSpace(code) || code.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var normalized = Normalize(code);
        if (Aliases.TryGetValue(normalized, out var alias))
        {
            normalized = alias;
        }

        if (!SupportedCodes.Contains(normalized))
        {
            return false;
        }

        supportedCode = normalized;
        return true;
    }

    private static string Normalize(string code)
    {
        var trimmed = code.Trim();
        var dashIdx = trimmed.IndexOf('-');
        var normalized = dashIdx > 0 ? trimmed[..dashIdx] : trimmed;
        return normalized.ToLowerInvariant();
    }
}
