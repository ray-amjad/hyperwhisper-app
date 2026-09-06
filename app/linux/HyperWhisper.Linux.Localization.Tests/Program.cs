using System.ComponentModel;
using System.Globalization;
using Avalonia.Media;
using HyperWhisper.Localization;
using HyperWhisper.Linux.Localization;
using System.Text.RegularExpressions;

var tests = new (string Name, Action Run)[]
{
    ("en de ar and zh-Hans resolve", SmokeCulturesResolve),
    ("missing culture and key fall back safely", MissingCultureAndKeyFallBack),
    ("runtime culture switch updates bindings", RuntimeCultureSwitchUpdatesBindings),
    ("RTL maps to Avalonia flow direction", RtlFlowDirectionMaps),
    ("format arguments are validated", FormatArgumentsAreValidated),
    ("provider and model identifiers remain opaque", IdentifiersRemainOpaque),
    ("disposed resources detach and clear notifications", DisposedResourcesDetach),
    ("disposed bridge releases subscribers", DisposedBridgeReleasesSubscribers),
    ("every Linux key falls back in every supported locale", LinuxCatalogFallbacks),
    ("every supported locale has exact reusable Linux translations", LinuxSatelliteCompleteness),
    ("Linux-specific copy remains an explicit invariant fallback", LinuxSpecificFallbackIsExplicit),
    ("production XAML contains no localizable literals", ProductionXamlHasNoLocalizableLiterals),
    ("production code uses catalogued user feedback", ProductionCodeUsesCataloguedFeedback),
    ("settings pages carry no Windows-only control or copy", SettingsPagesMatchWindowsSurface),
    ("tray labels are catalogued with RTL metadata", TrayLabelsAreCatalogued),
    ("startup culture selection is bounded", StartupCultureSelectionIsBounded),
};

var failures = 0;
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.WriteLine($"FAIL {test.Name}: {exception.Message}");
    }
}

Console.WriteLine($"{tests.Length - failures}/{tests.Length} tests passed");
return failures == 0 ? 0 : 1;

static void SmokeCulturesResolve()
{
    using var bridge = new AvaloniaLocalizationBridge(CultureInfo.GetCultureInfo("en"));
    Equal("Cancel", bridge.GetRequired("common.cancel"), "English");
    foreach (var cultureName in new[] { "de", "ar", "zh-Hans" })
    {
        bridge.SetCulture(CultureInfo.GetCultureInfo(cultureName));
        NotBlank(bridge.GetRequired("common.cancel"), cultureName);
        NotEqual("Cancel", bridge.GetRequired("common.cancel"), cultureName);
    }
}

static void MissingCultureAndKeyFallBack()
{
    using var bridge = new AvaloniaLocalizationBridge(CultureInfo.GetCultureInfo("en-NZ"));
    Equal("General", bridge.GetRequired("settings.nav.general"), "unsupported regional culture");
    Equal("linux.missing.key", bridge["linux.missing.key"], "dynamic missing key");
    Throws<KeyNotFoundException>(() => bridge.GetRequired("linux.missing.key"));
    Throws<KeyNotFoundException>(() => bridge.Bind("linux.missing.key"));
}

static void RuntimeCultureSwitchUpdatesBindings()
{
    using var bridge = new AvaloniaLocalizationBridge(CultureInfo.GetCultureInfo("en"));
    using var resource = bridge.Bind("common.cancel");
    var before = resource.Value;
    var resourceChanges = 0;
    var bridgeChanges = new List<string?>();
    resource.PropertyChanged += (_, args) =>
    {
        if (args.PropertyName == nameof(LocalizedResource.Value)) resourceChanges++;
    };
    bridge.PropertyChanged += (_, args) => bridgeChanges.Add(args.PropertyName);

    bridge.SetCulture(CultureInfo.GetCultureInfo("de"));

    NotEqual(before, resource.Value, "bound value");
    Equal(1, resourceChanges, "resource notification count");
    True(bridgeChanges.Contains(nameof(AvaloniaLocalizationBridge.Culture)), "culture notification");
    True(bridgeChanges.Contains(nameof(AvaloniaLocalizationBridge.FlowDirection)), "flow notification");
    True(bridgeChanges.Contains("Item[]"), "indexer notification");

    bridge.SetCulture(CultureInfo.GetCultureInfo("de"));
    Equal(1, resourceChanges, "unchanged culture does not notify");
}

static void RtlFlowDirectionMaps()
{
    using var bridge = new AvaloniaLocalizationBridge(CultureInfo.GetCultureInfo("de"));
    Equal(FlowDirection.LeftToRight, bridge.FlowDirection, "German");
    bridge.SetCulture(CultureInfo.GetCultureInfo("ar"));
    Equal(FlowDirection.RightToLeft, bridge.FlowDirection, "Arabic");
    bridge.SetCulture(CultureInfo.GetCultureInfo("zh-Hans"));
    Equal(FlowDirection.LeftToRight, bridge.FlowDirection, "Simplified Chinese");
}

static void FormatArgumentsAreValidated()
{
    using var bridge = new AvaloniaLocalizationBridge(CultureInfo.GetCultureInfo("de"));
    using var resource = bridge.BindFormat("settings.models.empty.filtered", "Vulkan");
    Contains("Vulkan", resource.Value, "formatted binding");
    Throws<LocalizationFormatException>(() => bridge.Format("settings.models.empty.filtered"));
    Throws<LocalizationFormatException>(() => bridge.BindFormat("settings.models.empty.filtered", "one", "two"));
}

static void IdentifiersRemainOpaque()
{
    Equal("openai", AvaloniaLocalizationBridge.ProviderIdentifier("openai"), "provider");
    Equal("whisper-1", AvaloniaLocalizationBridge.ModelIdentifier("whisper-1"), "model");
    Throws<ArgumentException>(() => AvaloniaLocalizationBridge.ProviderIdentifier(" "));
}

static void DisposedResourcesDetach()
{
    using var bridge = new AvaloniaLocalizationBridge(CultureInfo.GetCultureInfo("en"));
    var resource = bridge.Bind("common.cancel");
    var notifications = 0;
    resource.PropertyChanged += OnChanged;
    resource.Dispose();
    resource.Dispose();

    bridge.SetCulture(CultureInfo.GetCultureInfo("de"));

    Equal(0, notifications, "notification after disposal");
    Throws<ObjectDisposedException>(() => _ = resource.Value);
    return;

    void OnChanged(object? sender, PropertyChangedEventArgs args) => notifications++;
}

static void DisposedBridgeReleasesSubscribers()
{
    var bridge = new AvaloniaLocalizationBridge(CultureInfo.GetCultureInfo("en"));
    using var resource = bridge.Bind("common.cancel");
    bridge.Dispose();
    bridge.Dispose();
    Throws<ObjectDisposedException>(() => bridge.SetCulture(CultureInfo.GetCultureInfo("de")));
    Throws<ObjectDisposedException>(() => _ = resource.Value);
}

static void LinuxCatalogFallbacks()
{
    foreach (var culture in PortableLocalizer.SupportedCultures.Prepend(CultureInfo.GetCultureInfo("en")))
    {
        using var bridge = new AvaloniaLocalizationBridge(culture);
        foreach (var key in AvaloniaLocalizationBridge.LinuxCatalogKeys)
            NotBlank(bridge.GetRequired(key), $"{culture.Name}:{key}");
    }
}

static void LinuxSatelliteCompleteness()
{
    IReadOnlySet<string>? expected = null;
    foreach (var culture in PortableLocalizer.SupportedCultures)
    {
        var keys = AvaloniaLocalizationBridge.LinuxTranslatedKeys(culture);
        True(keys.Count >= 7, $"{culture.Name} Linux satellite is incomplete");
        expected ??= keys;
        True(expected.SetEquals(keys), $"{culture.Name} reusable-key set differs");
    }
    foreach (var cultureName in new[] { "de", "ar", "zh-Hans" })
    {
        using var bridge = new AvaloniaLocalizationBridge(CultureInfo.GetCultureInfo(cultureName));
        NotEqual("Import", bridge.GetRequired("linux.ui.import"), $"{cultureName} exact reused translation");
    }
}

static void LinuxSpecificFallbackIsExplicit()
{
    foreach (var cultureName in new[] { "de", "ar", "zh-Hans" })
    {
        var culture = CultureInfo.GetCultureInfo(cultureName);
        using var bridge = new AvaloniaLocalizationBridge(culture);
        True(!AvaloniaLocalizationBridge.LinuxTranslatedKeys(culture).Contains("linux.update.instructions"),
            $"{cultureName} unreviewed Linux-only key was marked translated");
        Equal("Updates come from your distribution's package manager, not from HyperWhisper.",
            bridge.GetRequired("linux.update.instructions"), $"{cultureName} invariant fallback");
    }

    using var arabic = new AvaloniaLocalizationBridge(CultureInfo.GetCultureInfo("ar"));
    Equal(FlowDirection.RightToLeft, arabic.FlowDirection, "Arabic satellite RTL flow");
}

static void ProductionXamlHasNoLocalizableLiterals()
{
    var directory = Path.Combine(AppContext.BaseDirectory, "LocalizationSurface");
    var allowedOpaqueValues = new HashSet<string>(StringComparer.Ordinal)
    {
        // "H" is the brand mark on the About page and "·" separates the two halves of a history
        // row, the same way Windows writes Text=" · ". Neither is copy, so neither is translated.
        // The Vocabulary key caps read "Ctrl" and the return arrow on every locale, the way a
        // keyboard is labelled; "›" is the Getting-started row chevron and " · " is the same
        // separator Windows writes as Text=" · ". None of the five is copy.
        // "−" and "+" are the two halves of the clipboard restore-delay stepper, the arithmetic
        // signs Windows also writes as literal Content. Neither is copy.
        "!", "✨", "×", "·", " · ", "›", "↵", "Ctrl", "H", "−", "+",
        "WAV, MP3, M4A, FLAC, OGG, or WebM", "/path/to/vocabulary.tsv",
        "gpt-4.1-mini", "anthropic:claude-haiku-4-5", "gemma-4-E2B-it-Q4_K_M.gguf",
        "https://host/v1/chat/completions", "auto",
    };
    var attribute = new Regex("(?:Title|Text|Content|PlaceholderText|Header)=\\\"(?<value>[^\\\"]+)\\\"",
        RegexOptions.CultureInvariant);
    foreach (var path in Directory.GetFiles(directory, "*.axaml"))
    {
        var source = File.ReadAllText(path);
        True(!source.Contains("StringFormat=", StringComparison.Ordinal), $"hard-coded StringFormat in {path}");
        foreach (Match match in attribute.Matches(source))
        {
            var value = match.Groups["value"].Value;
            True(value.StartsWith('{') || allowedOpaqueValues.Contains(value),
                $"localizable literal '{value}' in {path}");
        }
    }
}

static void ProductionCodeUsesCataloguedFeedback()
{
    var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
        "LocalizationSurface", "MainWindow.axaml.cs"));
    string[] forbidden =
    [
        "Title = \\\"", "new FilePickerFileType(\\\"", "PlatformStatusText.Text = $\\\"",
        "PlatformStatusText.Text += $\\\"", "SetStorageText(\\\"StorageStatusText\\\", \\\"",
    ];
    foreach (var value in forbidden)
        True(!source.Contains(value, StringComparison.Ordinal), $"uncatalogued feedback pattern: {value}");
}

static void SettingsPagesMatchWindowsSurface()
{
    var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
        "LocalizationSurface", "MainWindow.axaml"));

    // The settings pages must carry no control Windows does not draw. Windows has no word
    // timestamp switch anywhere (it keeps the value as opaque JSON: Models/UniversalBackupModels
    // .cs:144-149) and no default-language box on any settings page, so neither does this app.
    // Word timestamps are stored unconditionally, which is what the shared default already does.
    True(!source.Contains("SettingsStoreWordTimestamps", StringComparison.Ordinal),
        "the word timestamp checkbox is back; Windows has no such control");
    True(!source.Contains("SettingsLanguageInput", StringComparison.Ordinal),
        "the default-language box is back; Windows picks a language per mode, not per app");

    // Copy that names another platform is worse than no copy. The shared
    // settings.output.hideClipboardHistory.subtitle says "Windows clipboard history", so the page
    // must resolve the Linux override instead of the shared key.
    True(!source.Contains("[settings.output.hideClipboardHistory.subtitle]", StringComparison.Ordinal),
        "the clipboard-history row uses the shared subtitle, which names Windows");
    True(source.Contains("[linux.settings.output.hideClipboardHistory.subtitle]", StringComparison.Ordinal),
        "the clipboard-history row is missing its Linux subtitle override");

    using var bridge = new AvaloniaLocalizationBridge(CultureInfo.GetCultureInfo("en"));
    var subtitle = bridge.GetRequired("linux.settings.output.hideClipboardHistory.subtitle");
    Contains("clipboard history", subtitle, "clipboard subtitle");
    True(!subtitle.Contains("Windows", StringComparison.OrdinalIgnoreCase),
        "the Linux clipboard subtitle still names Windows");
}

static void TrayLabelsAreCatalogued()
{
    var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
        "LocalizationSurface", "status-notifier.py"));
    True(source.Contains("TRAY_CATALOG", StringComparison.Ordinal), "tray catalog missing");
    True(source.Contains("label(\"sidebar.history\")", StringComparison.Ordinal), "history label is literal");
    True(source.Contains("CULTURE in RTL_CULTURES", StringComparison.Ordinal), "tray RTL metadata missing");
    True(!Regex.IsMatch(source, @"node\(\d+,\s*""[A-Za-z]"), "literal tray menu label");
}

static void StartupCultureSelectionIsBounded()
{
    Equal("de", AvaloniaLocalizationBridge.ResolveStartupCulture("de-DE").Name, "regional German");
    Equal("zh-Hans", AvaloniaLocalizationBridge.ResolveStartupCulture("zh-CN").Name, "Simplified Chinese");
    Equal("en", AvaloniaLocalizationBridge.ResolveStartupCulture("eo").Name, "unsupported locale");
    Equal("en", AvaloniaLocalizationBridge.ResolveStartupCulture("not_a_culture!").Name, "invalid locale");
}

static void Equal<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{message}: expected '{expected}', got '{actual}'.");
}

static void NotEqual<T>(T unexpected, T actual, string message)
{
    if (EqualityComparer<T>.Default.Equals(unexpected, actual))
        throw new InvalidOperationException($"{message}: unexpectedly got '{actual}'.");
}

static void NotBlank(string value, string message)
{
    if (string.IsNullOrWhiteSpace(value))
        throw new InvalidOperationException($"{message}: value is blank.");
}

static void Contains(string expected, string actual, string message)
{
    if (!actual.Contains(expected, StringComparison.Ordinal))
        throw new InvalidOperationException($"{message}: '{actual}' does not contain '{expected}'.");
}

static void True(bool value, string message)
{
    if (!value) throw new InvalidOperationException($"{message}: expected true.");
}

static void Throws<T>(Action action) where T : Exception
{
    try
    {
        action();
    }
    catch (T)
    {
        return;
    }

    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}
