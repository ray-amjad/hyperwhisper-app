namespace HyperWhisper.CloudAccount;

/// <summary>
/// The loose shape check every account-key field runs before it sends anything (#1489).
/// <para>
/// It refuses only text that cannot be any key: empty after trimming, longer than
/// <see cref="MaximumLength"/> characters, or holding whitespace, a control character, or
/// any character outside printable ASCII. There is deliberately no <c>HW-XXXX</c> pattern:
/// the server also holds imported Polar-era keys whose format the app cannot assume, and
/// those must still activate. The server stays the only judge of whether a key is real.
/// </para>
/// <para>
/// macOS mirrors this rule in <c>LicenseKeyFormat</c> (Models/LicenseValidationResult.swift).
/// </para>
/// </summary>
public static class AccountKeyFormat
{
    /// <summary>The longest trimmed text that can still be a key.</summary>
    public const int MaximumLength = 64;

    /// <summary>
    /// The field text with leading and trailing whitespace and newlines removed. A key copied
    /// from an email often carries a trailing newline; this is the text that is checked and sent.
    /// </summary>
    public static string Normalize(string? text) => (text ?? string.Empty).Trim();

    /// <summary>True when the trimmed text could be a key, so Activate may send it.</summary>
    public static bool IsPlausible(string? text)
    {
        var key = Normalize(text);
        if (key.Length == 0 || key.Length > MaximumLength)
            return false;

        foreach (var character in key)
        {
            // Printable ASCII without the space: '!' (0x21) through '~' (0x7E). This one range
            // excludes every whitespace, every control character and every non-ASCII character.
            if (character < '!' || character > '~')
                return false;
        }

        return true;
    }

    /// <summary>
    /// True when the field holds text that cannot be a key, so the hint shows under it. An
    /// empty or whitespace-only field shows no hint: there is nothing to correct yet, and the
    /// disabled Activate button already says so.
    /// </summary>
    public static bool ShowsHint(string? text) =>
        Normalize(text).Length > 0 && !IsPlausible(text);
}
