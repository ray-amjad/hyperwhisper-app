using HyperWhisper.PortableApplication.Persistence;
using HyperWhisper.SharedCore;

var timestamp = new DateTime(2026, 8, 23, 12, 34, 56, DateTimeKind.Utc);
await using var stt = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "cloud-stt-catalog.json"));
await using var postProcessing = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "cloud-pp-catalog.json"));
var modes = PortableModeDefaults.CreateForRegion("gb", stt, postProcessing, timestamp);

Assert(modes.Count == 6, "expected six default modes");
Assert(modes.Select(mode => mode.Id).SequenceEqual(new[]
{
    PortableModeDefaults.HyperModeId,
    PortableModeDefaults.VoiceToTextModeId,
    PortableModeDefaults.MessageModeId,
    PortableModeDefaults.MailModeId,
    PortableModeDefaults.NoteModeId,
    PortableModeDefaults.MeetingModeId
}), "well-known mode IDs or order changed");
Assert(modes.Select(mode => mode.Name).SequenceEqual(new[] { "Hyper", "Voice to Text", "Message", "Mail", "Note", "Meeting" }),
    "default mode names changed");
Assert(modes.Select(mode => mode.Preset).SequenceEqual(new[] { "hyper", "hyper", "message", "mail", "note", "meeting" }),
    "default presets changed");
Assert(modes.Single(mode => mode.IsDefault).Id == PortableModeDefaults.HyperModeId, "Hyper is not the sole default");
Assert(modes.All(mode => mode.IsSystemProvided && mode.ProviderType == "cloud" && mode.CloudProvider == "hyperwhisper"),
    "a default mode is not a system HyperWhisper Cloud mode");
Assert(modes.All(mode => mode.CloudAccuracyTier == "elevenLabsScribeV2" && mode.CloudTranscriptionModel == "scribe_v2"),
    "STT defaults did not resolve from the shared catalog");
Assert(modes.All(mode => mode.CloudPostProcessingModel == "anthropic:claude-haiku-4-5"),
    "post-processing defaults did not resolve from the shared catalog");
Assert(modes.Single(mode => mode.Id == PortableModeDefaults.VoiceToTextModeId).PostProcessingMode == 0
    && modes.Where(mode => mode.Id != PortableModeDefaults.VoiceToTextModeId).All(mode =>
        mode.PostProcessingMode == 1 && mode.PostProcessingProvider == "hyperwhispercloud"),
    "post-processing seed policy does not match the established defaults");
Assert(modes.All(mode => mode.EnglishSpelling == "british" && mode.Language == "auto"), "GB locale seed is incorrect");
Assert(modes.All(mode => mode.CreatedDate == timestamp && mode.ModifiedDate == timestamp), "seed timestamps are not stable UTC values");

// The ISO 3166-1 region table lives in the shared Rust core (hw-text,
// EnglishSpelling::for_region), so everything below runs the real FFI through
// SharedCoreBridge. That makes this the portable-head half of a cross-platform
// parity check: macOS EnglishSpellingRegionDefaultTests.swift and the Windows
// SmokeTests assert the same codes against the same table.
Assert(PortableModeDefaults.EnglishSpellingForRegion("CA") == "canadian", "Canadian spelling mapping failed");
Assert(PortableModeDefaults.EnglishSpellingForRegion("AU") == "australian", "Australian spelling mapping failed");
Assert(PortableModeDefaults.EnglishSpellingForRegion("NF") == "australian", "Australian territory spelling mapping failed");
Assert(PortableModeDefaults.EnglishSpellingForRegion("GB") == "british", "British spelling mapping failed");
Assert(PortableModeDefaults.EnglishSpellingForRegion("IE") == "british", "Irish spelling mapping failed");
Assert(PortableModeDefaults.EnglishSpellingForRegion("NZ") == "british", "British-compatible spelling mapping failed");
Assert(PortableModeDefaults.EnglishSpellingForRegion("IN") == "british", "Indian spelling mapping failed");
Assert(PortableModeDefaults.EnglishSpellingForRegion("US") == "american", "American spelling mapping failed");
Assert(PortableModeDefaults.EnglishSpellingForRegion("JP") == "american", "unlisted region fallback failed");
Assert(PortableModeDefaults.EnglishSpellingForRegion("ZZ") == "american", "invalid region fallback failed");
Assert(PortableModeDefaults.EnglishSpellingForRegion(null) == "american", "unknown region fallback failed");
Assert(PortableModeDefaults.EnglishSpellingForRegion("") == "american", "empty region fallback failed");
Assert(PortableModeDefaults.EnglishSpellingForRegion("   ") == "american", "whitespace region fallback failed");

// Trimming and case folding are the core's, not this head's.
Assert(PortableModeDefaults.EnglishSpellingForRegion("gb") == "british", "lowercase region code was not folded");
Assert(PortableModeDefaults.EnglishSpellingForRegion(" au ") == "australian", "padded region code was not trimmed");
Assert(PortableModeDefaults.EnglishSpellingForRegion("\nca\n") == "canadian", "newline-padded region code was not trimmed");

// A SEEDING call must never answer with the empty token. Empty means "the user
// never chose", which suppresses the spelling instruction entirely at prompt
// time — a different thing from american, and never a thing to seed.
foreach (var code in new string?[] { "GB", "AU", "CA", "US", "JP", "ZZ", "", "   ", " gb ", null })
{
    Assert(SharedCoreBridge.EnglishSpellingForRegion(code).Length > 0,
        $"region '{code ?? "<null>"}' seeded the empty no-spelling token");
}

await AssertInvalidCatalogAsync("""{"providers": [{"id":"elevenLabsScribeV2","models":[]}]}""", isStt: true);
await AssertInvalidCatalogAsync("""{"providers": [{"id":"anthropic","enabled":false,"models":[{"id":"claude-haiku-4-5","isDefault":true}]}]}""", isStt: false);

Console.WriteLine("Portable mode defaults verification passed (6 modes, shared catalogs, locale spelling, invalid-catalog fail-closed).");

static async Task AssertInvalidCatalogAsync(string invalidJson, bool isStt)
{
    await using var validStt = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "cloud-stt-catalog.json"));
    await using var validPostProcessing = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "cloud-pp-catalog.json"));
    await using var invalid = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(invalidJson));
    try
    {
        _ = PortableModeDefaults.CreateForRegion("US", isStt ? invalid : validStt, isStt ? validPostProcessing : invalid,
            new DateTime(2026, 8, 23, 0, 0, 0, DateTimeKind.Utc));
    }
    catch (InvalidDataException)
    {
        return;
    }
    throw new InvalidOperationException("an invalid shared catalog did not fail closed");
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
