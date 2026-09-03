using System.Globalization;
using System.Reflection;
using HyperWhisper.Localization;

var tests = new (string Name, Action Run)[]
{
    ("all 40 catalogs load", AllCatalogsLoad),
    ("smoke cultures localize", SmokeCulturesLocalize),
    ("parent and missing cultures fall back", MissingCulturesFallBack),
    ("missing keys are rejected", MissingKeysAreRejected),
    ("formatting is validated and culture aware", FormattingIsValidated),
    ("legacy placeholders remain usable", LegacyPlaceholdersRemainUsable),
    ("RTL cultures are identified", RtlCulturesAreIdentified),
    ("routing and persisted identifiers remain opaque", IdentifiersRemainOpaque),
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

static void AllCatalogsLoad()
{
    Equal(39, PortableLocalizer.SupportedCultures.Count, "translated culture count");
    // 658 (the count before this feature) + the nine keys Gemini 3.5 Transcribe
    // added, in two groups that landed in DIFFERENT commits.
    //
    // THIS NUMBER IS NOT DERIVED — it is a hand-maintained expectation, and the
    // only thing that keeps `Strings.resx` from growing keys nobody translated.
    // It has to be bumped in the same commit that adds the key. It was not, both
    // times: the four BYOK keys landed with the backend phase and left this at
    // 658, and the five `settings.streaming.*` keys landed with the Windows
    // phase-5 commit and left it at 662 — which is why this suite was red from
    // that commit until this line moved to 667, through the macOS phase-5 commit
    // (which does not touch `Strings.resx` at all) and the one after it. A red
    // localization suite is what a missing translation looks like here, so a
    // stale number here does not just fail CI, it hides the next missing key.
    //
    // Four for the BYOK pre-recorded provider:
    //   provider.geminiTranscribe
    //   mode.editor.provider.geminiTranscribe.tooltip
    //   settings.api.invalidKey.geminiTranscribe
    //   settings.api.provider.geminiTranscribe.description
    //
    // Five for the live/streaming surface — the BYOK streaming provider row and
    // its two status strings, plus the HyperWhisper Cloud live tier picker that
    // the tier now needs a choice in:
    //   settings.streaming.provider.geminiTranscribe
    //   settings.streaming.providerStatus.geminiTranscribe.configured
    //   settings.streaming.providerStatus.geminiTranscribe.missingKey
    //   settings.streaming.cloudTier.title
    //   settings.streaming.cloudTier.subtitle
    //
    // Four for the direct Meta BYOK provider:
    //   provider.meta
    //   mode.editor.provider.meta.tooltip
    //   settings.api.invalidKey.meta
    //   settings.api.provider.meta.description
    //
    // The catalog-v8 tier rename is key-count neutral: googleChirp3's
    // label/description became geminiTranscribe's.
    Equal(849, PortableLocalizer.BaseKeyCount, "base key count");
    var english = new PortableLocalizer(CultureInfo.InvariantCulture);
    var key = english.Key("home.welcome.title");
    NotBlank(english.Get(key), "base value");
    foreach (var culture in PortableLocalizer.SupportedCultures)
    {
        NotBlank(new PortableLocalizer(culture).Get(key), culture.Name);
        var satellite = typeof(PortableLocalizer).Assembly.GetSatelliteAssembly(culture);
        True(
            satellite.GetManifestResourceNames().Contains(
                $"HyperWhisper.Localization.Resources.Strings.{culture.Name}.resources",
                StringComparer.Ordinal),
            $"{culture.Name} satellite catalog");
    }
}

static void SmokeCulturesLocalize()
{
    const string keyName = "common.cancel";
    var english = new PortableLocalizer(CultureInfo.GetCultureInfo("en"));
    var key = english.Key(keyName);
    Equal("Cancel", english.Get(key), "English fallback");
    NotEqual("Cancel", new PortableLocalizer(CultureInfo.GetCultureInfo("de")).Get(key), "German");
    NotEqual("Cancel", new PortableLocalizer(CultureInfo.GetCultureInfo("ar")).Get(key), "Arabic");
    NotEqual("Cancel", new PortableLocalizer(CultureInfo.GetCultureInfo("zh-Hans")).Get(key), "Simplified Chinese");
}

static void MissingCulturesFallBack()
{
    var key = new PortableLocalizer(CultureInfo.InvariantCulture).Key("settings.nav.general");
    Equal("General", new PortableLocalizer(CultureInfo.GetCultureInfo("en-NZ")).Get(key), "missing culture");
    Equal(
        new PortableLocalizer(CultureInfo.GetCultureInfo("de")).Get(key),
        new PortableLocalizer(CultureInfo.GetCultureInfo("de-AT")).Get(key),
        "parent culture");
}

static void MissingKeysAreRejected()
{
    var localizer = new PortableLocalizer();
    Equal("linux.key.that.does.not.exist", localizer.Get("linux.key.that.does.not.exist"), "dynamic key fallback");
    Equal(string.Empty, localizer.Get(string.Empty), "empty key fallback");
    Throws<KeyNotFoundException>(() => localizer.Key("linux.key.that.does.not.exist"));
    Throws<KeyNotFoundException>(() => new PortableLocalizer().Get(default(LocalizationKey)));
}

static void FormattingIsValidated()
{
    var localizer = new PortableLocalizer(CultureInfo.GetCultureInfo("de"));
    var key = localizer.Key("settings.models.empty.filtered");
    var formatted = localizer.Format(key, "Vulkan");
    Contains("Vulkan", formatted, "formatted argument");
    Throws<LocalizationFormatException>(() => localizer.Format(key));
    Throws<LocalizationFormatException>(() => localizer.Format(key, "one", "two"));
}

static void LegacyPlaceholdersRemainUsable()
{
    var localizer = new PortableLocalizer(CultureInfo.GetCultureInfo("de"));
    var key = localizer.Key("transcripts.delete.multiple.title");
    var formatted = localizer.Format(key, 12);
    Contains("12", formatted, "legacy %d normalization");
    DoesNotContain("%d", formatted, "legacy token");
}

static void RtlCulturesAreIdentified()
{
    True(new PortableLocalizer(CultureInfo.GetCultureInfo("ar")).IsRightToLeft, "Arabic");
    True(new PortableLocalizer(CultureInfo.GetCultureInfo("he")).IsRightToLeft, "Hebrew");
    False(new PortableLocalizer(CultureInfo.GetCultureInfo("de")).IsRightToLeft, "German");
    False(new PortableLocalizer(CultureInfo.GetCultureInfo("zh-Hans")).IsRightToLeft, "Chinese");
}

static void IdentifiersRemainOpaque()
{
    Equal("openai", PortableLocalizer.PreserveIdentifier(LocalizationIdentifierKind.Provider, "openai"), "provider");
    Equal("whisper-1", PortableLocalizer.PreserveIdentifier(LocalizationIdentifierKind.Model, "whisper-1"), "model");
    Equal("PushToTalk", PortableLocalizer.PreserveIdentifier(LocalizationIdentifierKind.PersistedValue, "PushToTalk"), "persisted value");
}

static void Equal<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{message}: expected '{expected}', got '{actual}'.");
    }
}

static void NotEqual<T>(T unexpected, T actual, string message)
{
    if (EqualityComparer<T>.Default.Equals(unexpected, actual))
    {
        throw new InvalidOperationException($"{message}: unexpectedly got '{actual}'.");
    }
}

static void NotBlank(string value, string message)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        throw new InvalidOperationException($"{message}: value is blank.");
    }
}

static void Contains(string expected, string actual, string message)
{
    if (!actual.Contains(expected, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"{message}: '{actual}' does not contain '{expected}'.");
    }
}

static void DoesNotContain(string unexpected, string actual, string message)
{
    if (actual.Contains(unexpected, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"{message}: '{actual}' contains '{unexpected}'.");
    }
}

static void True(bool value, string message)
{
    if (!value)
    {
        throw new InvalidOperationException($"{message}: expected true.");
    }
}

static void False(bool value, string message) => True(!value, message);

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
