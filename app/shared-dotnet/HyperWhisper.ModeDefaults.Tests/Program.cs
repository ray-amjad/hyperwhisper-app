// The .NET half of the first-run mode-seed contract (#285).
//
// Two things are proved here, against the Mode entity the heads actually seed
// rather than against a DTO:
//
//   1. PortableModeDefaults seeds EXACTLY ONE mode, with the agreed field
//      values. It is the only .NET seeder — Windows' ModeDefaults delegates to
//      it — so this is also Windows' coverage.
//   2. Those values match shared-conformance/mode-seed-vectors.json row for
//      row, the same file Rust and Swift run:
//
//        shared-core-rs/crates/hw-core/tests/mode_seed_vectors.rs
//        app/macos/hyperwhisperTests/ModeSeedConformanceVectorTests.swift
//
//      Regenerate the vectors from Rust after an INTENDED seed change:
//        cd shared-core-rs && cargo test -p hw-core --test mode_seed_vectors -- --ignored regenerate
//      A field that moves without a matching seed edit is a regression, not a
//      refresh.
//
// There is no longer an invalid-catalog case to assert. The seeder no longer
// parses a catalog: hw-catalog resolves both models from a catalog embedded at
// compile time and pins the answers with
// `catalog_resolution_matches_the_fallback_literals`. The fail-closed guarantee
// moved from "throw InvalidDataException at first launch" to "fail CI before
// the binary ships", which is strictly stronger.

using System.Text.Json;
using HyperWhisper.PortableApplication.Persistence;
using HyperWhisper.SharedCore;

var timestamp = new DateTime(2026, 8, 23, 12, 34, 56, DateTimeKind.Utc);
var modes = PortableModeDefaults.CreateForRegion("gb", timestamp);

Assert(modes.Count == 1, "expected exactly one default mode");
var hyper = modes[0];

// A fresh install used to get six modes here ("Hyper", "Voice to Text",
// "Message", "Mail", "Note", "Meeting") while macOS got one. One is the product
// decision; existing users keep whatever they have, because seeding only runs
// against an empty store.
Assert(hyper.Id == PortableModeDefaults.HyperModeId, "the seeded mode is not on the well-known id");
Assert(hyper.Name == "Hyper", "the seeded mode name changed");
Assert(hyper.Preset == "hyper", "the seeded preset changed");
Assert(hyper.IsDefault, "the seeded mode is not the default");
Assert(hyper.IsSystemProvided, "the seeded mode is not system-provided");
Assert(hyper.SortOrder == 0, "the seeded sort order changed");
Assert(hyper.Language == "auto", "the seeded mode does not auto-detect language");

// C7: ProviderType and Model are different columns. macOS overloads its `model`
// attribute with this token because it has no providerType; the .NET entity has
// both and the seeder must leave Model alone.
Assert(hyper.ProviderType == "cloud", "the seeded mode is not a cloud mode");
Assert(hyper.Model is null, "the seeder wrote Mode.Model, which is the local engine's column, not ProviderType's");

Assert(hyper.CloudProvider == "hyperwhisper", "the seeded cloud transcription provider changed");
Assert(hyper.CloudAccuracyTier == "elevenLabsScribeV2", "the seeded cloud accuracy tier changed");
Assert(hyper.CloudTranscriptionModel == "scribe_v2", "the STT model did not resolve from the shared catalog");

// The canonical stored spelling. Windows and Linux already fold "hyperwhisper"
// and "hyperwhisper_cloud" onto it; macOS learned to read all three before it
// started seeding this.
Assert(hyper.PostProcessingMode == 1, "the seeded mode does not post-process");
Assert(hyper.PostProcessingProvider == "hyperwhispercloud", "the seeded post-processing provider changed");

// C2: keep the engine:model prefix. macOS' parser falls back to GROK on a value
// it cannot split, so a bare model id would silently change the model there.
Assert(hyper.CloudPostProcessingModel == "anthropic:claude-haiku-4-5",
    "the post-processing model did not resolve from the shared catalog, or lost its engine prefix");

Assert(hyper.EnglishSpelling == "british", "GB locale seed is incorrect");
Assert(hyper is { Punctuation: true, Capitalization: true, ProfanityFilter: false },
    "the seeded text-treatment flags changed");

// Both were left unset by the old seeders, which meant they silently inherited
// the entity default rather than the agreed value.
Assert(hyper.CustomInstructions == string.Empty, "custom instructions were not seeded explicitly");

Assert(hyper.CreatedDate == timestamp && hyper.ModifiedDate == timestamp,
    "seed timestamps are not stable UTC values");

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

var vectorCount = AssertConformanceVectors();

Console.WriteLine(
    $"Portable mode defaults verification passed (1 mode, shared-core seed, locale spelling, {vectorCount} conformance vectors).");

int AssertConformanceVectors()
{
    var path = Path.Combine(AppContext.BaseDirectory, "mode-seed-vectors.json");
    if (!File.Exists(path))
        throw new FileNotFoundException($"mode-seed-vectors.json not found at {path}", path);

    using var document = JsonDocument.Parse(File.ReadAllText(path));
    if (!document.RootElement.TryGetProperty("seeds", out var seeds) || seeds.ValueKind != JsonValueKind.Array)
        throw new InvalidOperationException("mode-seed-vectors.json has no 'seeds' array");

    var count = 0;
    foreach (var vector in seeds.EnumerateArray())
    {
        // A JSON null region is the "the OS told us nothing" case, and it is a
        // different input from the empty string. Both are in the file.
        var regionElement = vector.GetProperty("region");
        var region = regionElement.ValueKind == JsonValueKind.Null ? null : regionElement.GetString();
        var label = region is null ? "<null>" : $"'{region}'";

        // Asserted through the entity, not through the seed record: the
        // seed -> Mode mapping is where a head can still get this wrong.
        var seeded = PortableModeDefaults.CreateForRegion(region, timestamp);
        Assert(seeded.Count == 1, $"vector {label}: expected exactly one seeded mode");
        var mode = seeded[0];

        AssertVector(vector, label, "id", mode.Id.ToString("D"));
        AssertVector(vector, label, "name", mode.Name);
        AssertVector(vector, label, "preset", mode.Preset);
        AssertVector(vector, label, "language", mode.Language);
        AssertVector(vector, label, "providerType", mode.ProviderType);
        AssertVector(vector, label, "cloudProvider", mode.CloudProvider);
        AssertVector(vector, label, "cloudAccuracyTier", mode.CloudAccuracyTier);
        AssertVector(vector, label, "cloudTranscriptionModel", mode.CloudTranscriptionModel);
        AssertVector(vector, label, "postProcessingProvider", mode.PostProcessingProvider);
        AssertVector(vector, label, "cloudPostProcessingModel", mode.CloudPostProcessingModel);
        AssertVector(vector, label, "englishSpelling", mode.EnglishSpelling);
        AssertVector(vector, label, "customInstructions", mode.CustomInstructions);
        AssertVector(vector, label, "postProcessingMode", mode.PostProcessingMode);
        AssertVector(vector, label, "sortOrder", mode.SortOrder);
        AssertVector(vector, label, "punctuation", mode.Punctuation);
        AssertVector(vector, label, "capitalization", mode.Capitalization);
        AssertVector(vector, label, "profanityFilter", mode.ProfanityFilter);
        AssertVector(vector, label, "isDefault", mode.IsDefault);
        AssertVector(vector, label, "isSystemProvided", mode.IsSystemProvided);

        // The bridge record and the entity must not drift apart either: the
        // mapping above is the only thing between them.
        var raw = SharedCoreBridge.ModeSeedDefault(region);
        Assert(Guid.Parse(raw.Id) == PortableModeDefaults.HyperModeId,
            $"vector {label}: the shared seed's id is not PortableModeDefaults.HyperModeId");
        Assert(raw.Name == mode.Name && raw.EnglishSpelling == mode.EnglishSpelling
            && raw.PostProcessingProvider == mode.PostProcessingProvider
            && raw.CloudPostProcessingModel == mode.CloudPostProcessingModel,
            $"vector {label}: the seed record and the seeded Mode disagree");
        count++;
    }

    Assert(count > 0, "mode-seed-vectors.json contained no vectors");
    return count;
}

void AssertVector(JsonElement vector, string label, string field, object? actual)
{
    var element = vector.GetProperty(field);
    object? expected = element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.GetInt32(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new InvalidOperationException($"vector {label}: field '{field}' has an unsupported JSON kind")
    };
    Assert(Equals(expected, actual),
        $"vector {label}: field '{field}' expected '{expected}', got '{actual ?? "<null>"}'");
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
