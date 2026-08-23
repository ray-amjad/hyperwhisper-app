using System.ComponentModel;
using System.Globalization;
using Avalonia.Media;
using HyperWhisper.Localization;
using HyperWhisper.Linux.Localization;

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
