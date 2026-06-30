using System.Windows.Markup;

namespace HyperWhisper.Localization;

/// <summary>
/// XAML markup extension for localized strings.
/// Allows using localized strings directly in XAML via {loc:Loc key}.
///
/// Usage in XAML:
///   1. Add namespace: xmlns:loc="clr-namespace:HyperWhisper.Localization"
///   2. Use: Text="{loc:Loc home.welcome.title}"
///
/// Note: This provides design-time support but the strings are resolved
/// at runtime based on the current culture.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public class LocExtension : MarkupExtension
{
    /// <summary>
    /// The resource key to look up (e.g., "home.welcome.title")
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Default constructor required for XAML parser
    /// </summary>
    public LocExtension()
    {
    }

    /// <summary>
    /// Constructor with key parameter for shorthand syntax: {loc:Loc home.welcome.title}
    /// </summary>
    /// <param name="key">The resource key to look up</param>
    public LocExtension(string key)
    {
        Key = key;
    }

    /// <summary>
    /// Provides the localized string value for the XAML parser
    /// </summary>
    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        if (string.IsNullOrEmpty(Key))
            return "[Missing Key]";

        return Loc.S(Key);
    }
}
