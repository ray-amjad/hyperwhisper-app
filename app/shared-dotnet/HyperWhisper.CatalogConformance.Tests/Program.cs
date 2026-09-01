// Runs shared-conformance/catalog-vectors.json against the C# UniFFI binding.
//
// Issue #280 deleted the native catalog decoders, so there is exactly one
// implementation of the polymorphic decoding, the vendor grouping and the
// picker-language folding. These vectors prove the C# head reads that one
// implementation's answer unchanged. Rust and Swift run the same file:
//
//   shared-core-rs/crates/hw-core/tests/catalog_vectors.rs
//   app/macos/hyperwhisperTests/CatalogConformanceVectorTests.swift
//
// Regenerate the vectors from Rust after an intended catalog change:
//   cd shared-core-rs && cargo test -p hw-core --test catalog_vectors -- --ignored regenerate

using System.Text.Json;
using HyperWhisper.SharedCore;
using uniffi.hyperwhisper_core;

var path = Path.Combine(AppContext.BaseDirectory, "catalog-vectors.json");
if (!File.Exists(path))
    throw new FileNotFoundException($"catalog-vectors.json not found at {path}", path);

using var document = JsonDocument.Parse(File.ReadAllText(path));
var root = document.RootElement;

var checks = new (string Name, Action Run)[]
{
    ("polymorphic catalog decoding matches the vectors", CheckEntries),
    ("vendor grouping matches the vectors", CheckVendorGroups),
    ("picker-language folding matches the vectors", CheckPickerLanguages),
    ("models-catalog lookups match the vectors", CheckModelsEntries),
    ("the vectors cover every polymorphic branch", CheckCoverage),
    ("streaming cloud tiers are exactly the vendors we serve a WS route for", CheckStreamingCloudTiers),
    ("every head's live-only model set matches shared-conformance", CheckLiveOnlyModelIds),
};

foreach (var check in checks)
{
    check.Run();
    Console.WriteLine($"PASS {check.Name}");
}
Console.WriteLine($"Catalog conformance: {checks.Length}/{checks.Length} checks passed.");

// ---------------------------------------------------------------------------

void CheckEntries()
{
    var expected = root.GetProperty("cloudSttEntries").EnumerateArray().ToArray();
    var actual = HyperwhisperCoreMethods.CloudSttEntries();
    Equal(expected.Length, actual.Count, "cloud-STT provider count");

    for (var i = 0; i < expected.Length; i++)
    {
        var want = expected[i];
        var got = actual[i];
        var id = Str(want, "id")!;
        Equal(id, got.@id, "entry order");

        Equal(Str(want, "displayName"), got.@displayName, $"{id}.displayName");
        Equal(Str(want, "displayModel"), got.@displayModel, $"{id}.displayModel");
        Equal(Str(want, "vendor"), got.@vendor, $"{id}.vendor");
        Equal(Str(want, "vendorDisplayName"), got.@vendorDisplayName, $"{id}.vendorDisplayName");
        Equal(Str(want, "vendorLabel"), got.@vendorLabel, $"{id}.vendorLabel");
        Equal(Str(want, "sttProvider"), got.@sttProvider, $"{id}.sttProvider");

        Equal(Bool(want, "cloudTierEligible"), got.@access?.@cloudTierEligible, $"{id}.access.cloudTierEligible");
        Equal(Bool(want, "byokEligible"), got.@access?.@byokEligible, $"{id}.access.byokEligible");
        Equal(Str(want, "cloudTierAccuracy"), got.@cloudTier?.@accuracy, $"{id}.cloudTier.accuracy");
        Equal(Num(want, "cloudTierCreditsPerMinute"), got.@cloudTier?.@creditsPerMinute, $"{id}.cloudTier.creditsPerMinute");

        Equal(Bool(want, "wordTimestamps"), got.@features.@wordTimestamps, $"{id}.features.wordTimestamps");
        Equal(Bool(want, "diarization"), got.@features.@diarization, $"{id}.features.diarization");
        Equal(Bool(want, "streaming"), got.@features.@streaming, $"{id}.features.streaming");
        Equal(Bool(want, "codeSwitching"), got.@features.@codeSwitching, $"{id}.features.codeSwitching");
        Equal(Bool(want, "endpointing"), got.@features.@endpointing, $"{id}.features.endpointing");
        Equal(Bool(want, "contextBias"), got.@features.@contextBias, $"{id}.features.contextBias");
        Equal(Bool(want, "languageBias"), got.@features.@languageBias, $"{id}.features.languageBias");
        Equal(Bool(want, "turnTimestamps"), got.@features.@turnTimestamps, $"{id}.features.turnTimestamps");

        // The tri-state the bool-or-string field decodes to.
        Equal(Str(want, "customVocabularySupported"), VocabName(got.@customVocabulary?.@supported),
            $"{id}.customVocabulary.supported");
        Equal(Str(want, "customVocabularyFieldName"), got.@customVocabulary?.@fieldName,
            $"{id}.customVocabulary.fieldName");

        Equal(Int(want, "languagesCount"), got.@languages.@count, $"{id}.languages.count");
        Equal(Bool(want, "languagesAutoDetect"), got.@languages.@autoDetect, $"{id}.languages.autoDetect");
        Equal(Str(want, "languagesCodeFormat"), got.@languages.@codeFormat, $"{id}.languages.codeFormat");
        Equal(Bool(want, "languagesHasCodes"), got.@languages.@hasCodes, $"{id}.languages.hasCodes");

        var rawCodes = HyperwhisperCoreMethods.CloudSttLanguageCodes(id);
        Equal(Int(want, "languagesRawCodeCount"), (long)(rawCodes?.Count ?? 0), $"{id}.languages raw code count");
        Equal(Bool(want, "languagesHasCodes"), rawCodes is not null,
            $"{id}: hasCodes must agree with whether the code accessor returns a list");

        Equal(Num(want, "maxFileSizeMb"), got.@maxFileSizeMb, $"{id}.maxFileSizeMb");
        Equal(Int(want, "maxDurationMinutes"), got.@maxDurationMinutes, $"{id}.maxDurationMinutes");
        SequenceEqual(Strings(want, "acceptedFormats"), got.@acceptedFormats, $"{id}.acceptedFormats");
        Equal(Bool(want, "previewStatus"), got.@previewStatus, $"{id}.previewStatus");
        SequenceEqual(Strings(want, "migrateFrom"), got.@migrateFrom, $"{id}.migrateFrom");
        SequenceEqual(Strings(want, "legacyCloudProviderAliases"), got.@legacyCloudProviderAliases,
            $"{id}.legacyCloudProviderAliases");

        // An empty string is a real default model id (Grok); it must not
        // collapse into null, which is what an unknown provider returns.
        Equal(Str(want, "defaultModelId"), HyperwhisperCoreMethods.CloudSttDefaultModelId(id),
            $"{id}.defaultModelId");

        var wantModels = want.GetProperty("models").EnumerateArray().ToArray();
        Equal(wantModels.Length, got.@models.Count, $"{id}.models count");
        for (var m = 0; m < wantModels.Length; m++)
        {
            var wm = wantModels[m];
            var gm = got.@models[m];
            var label = $"{id}.models[{m}]";
            Equal(Str(wm, "id"), gm.@id, $"{label}.id");
            Equal(Str(wm, "displayName"), gm.@displayName, $"{label}.displayName");
            Equal(Num(wm, "creditsPerMinute"), gm.@creditsPerMinute, $"{label}.creditsPerMinute");
            Equal(Bool(wm, "isDefault"), gm.@isDefault, $"{label}.isDefault");
            Equal(Bool(wm, "previewStatus"), gm.@previewStatus, $"{label}.previewStatus");
            Equal(Bool(wm, "supportsCustomVocabulary"), gm.@supportsCustomVocabulary,
                $"{label}.supportsCustomVocabulary");
            Equal(Bool(wm, "streaming"), gm.@streaming, $"{label}.streaming");
        }
    }
}

void CheckVendorGroups()
{
    var expected = root.GetProperty("vendorGroups").EnumerateArray().ToArray();
    var actual = HyperwhisperCoreMethods.CloudSttCloudTierVendorGroups();
    // Order is the Provider dropdown's order, so it is part of the contract.
    Equal(expected.Length, actual.Count, "vendor group count");

    for (var i = 0; i < expected.Length; i++)
    {
        var want = expected[i];
        var got = actual[i];
        var key = Str(want, "vendorKey")!;
        Equal(key, got.@vendorKey, $"vendorGroups[{i}].vendorKey (dropdown order)");
        Equal(Str(want, "displayName"), got.@displayName, $"{key}.displayName");
        SequenceEqual(Strings(want, "entryIds"), got.@entries.Select(e => e.@id).ToList(), $"{key}.entries");
        SequenceEqual(
            Strings(want, "models"),
            got.@entries.SelectMany(e => e.@models.Select(m => $"{e.@id}/{m.@id}")).ToList(),
            $"{key}.models (each model tagged with the tier that routes it)");

        // The same group must be reachable by its key and by any of its tiers.
        Equal(key, HyperwhisperCoreMethods.CloudSttVendorGroupForVendorKey(key)?.@vendorKey,
            $"{key}: lookup by vendor key");
        foreach (var entryId in Strings(want, "entryIds"))
        {
            Equal(key, HyperwhisperCoreMethods.CloudSttVendorGroup(entryId)?.@vendorKey,
                $"{key}: lookup by member tier `{entryId}`");
        }
    }

    True(HyperwhisperCoreMethods.CloudSttVendorGroup("noSuchTier") is null,
        "an unknown tier id must not resolve to a vendor group");
}

// The eligible set for the HyperWhisper Cloud live tier picker. This is the guard
// that stops someone flipping a `models[].streaming` flag on a vendor we serve no
// backend WebSocket route for and shipping a 404 at dictation time: the STT
// catalog has no `enabled` gate to hide a half-finished vendor behind, and the
// client derives its route as `/ws/streaming-{sttProvider}` with no allow-list of
// its own. Widen this list only in the same change that adds the backend route.
//
// Deliberately NOT derived from the entry-level `features.streaming` hint, which
// is true for six vendors (grok, assemblyAI, mistral, soniox, …) that have no
// HyperWhisper Cloud live route at all.
void CheckStreamingCloudTiers()
{
    string[] expected = ["deepgramNova3", "geminiTranscribe"];
    SequenceEqual(expected, HyperwhisperCoreMethods.CloudSttStreamingCloudTierEntryIds(),
        "streaming cloud tier entry ids");

    // The picker shows a localized label per id and the route needs the vendor's
    // sttProvider, so every eligible id must resolve through both lookups.
    foreach (var id in expected)
    {
        var entry = HyperwhisperCoreMethods.CloudSttEntry(id);
        True(entry is not null, $"{id}: eligible for the live picker but absent from the catalog");
        True(entry!.@access?.@cloudTierEligible == true, $"{id}: live-eligible but not cloudTierEligible");
        True(!string.IsNullOrWhiteSpace(HyperwhisperCoreMethods.CloudSttProvider(id)),
            $"{id}: no sttProvider, so /ws/streaming-{{sttProvider}} cannot be derived");
        True(entry.@models.Any(model => model.@streaming == true),
            $"{id}: no model marked streaming, so the eligible set disagrees with the catalog");
    }
}

void CheckPickerLanguages()
{
    foreach (var want in root.GetProperty("pickerLanguageCodes").EnumerateArray())
    {
        var id = Str(want, "id")!;
        var got = HyperwhisperCoreMethods.CloudSttPickerLanguageCodes(id);
        if (want.GetProperty("codes").ValueKind == JsonValueKind.Null)
        {
            True(got is null,
                $"{id}: an unverified language set must fold to null so the picker keeps its full list");
            continue;
        }
        True(got is not null, $"{id}: expected a folded language set, got null");
        SequenceEqual(Strings(want, "codes"), got!, $"{id} picker language fold");
    }

    True(HyperwhisperCoreMethods.CloudSttPickerLanguageCodes("noSuchProvider") is null,
        "an unknown provider must fold to null");
}

void CheckModelsEntries()
{
    var expected = root.GetProperty("modelsEntries").EnumerateArray().ToArray();
    var actual = HyperwhisperCoreMethods.ModelsAllEntries();
    Equal(expected.Length, actual.Count, "models-catalog row count");

    for (var i = 0; i < expected.Length; i++)
    {
        var want = expected[i];
        var got = actual[i];
        var label = $"{Str(want, "provider")}/{Str(want, "kind")}/{Str(want, "id")}";
        Equal(Str(want, "provider"), got.@provider, $"{label}.provider");
        Equal(Str(want, "id"), got.@id, $"{label}.id");
        Equal(Str(want, "kind"), got.@kind, $"{label}.kind");
        Equal(Bool(want, "supportsCustomVocabulary"), got.@supportsCustomVocabulary,
            $"{label}.supportsCustomVocabulary");
        Equal(Bool(want, "availableViaHyperWhisperCloud"), got.@availableViaHyperWhisperCloud,
            $"{label}.availableViaHyperWhisperCloud");
        var wantCaps = want.TryGetProperty("voiceCapabilities", out var capsElement)
            && capsElement.ValueKind != JsonValueKind.Null ? capsElement : (JsonElement?)null;
        Equal(wantCaps is null, got.@voiceCapabilities is null, $"{label}.voiceCapabilities presence");
        if (wantCaps is { } wc && got.@voiceCapabilities is { } gc)
        {
            Equal(Bool(wc, "codeSwitching"), gc.@codeSwitching, $"{label}.voiceCapabilities.codeSwitching");
            Equal(Bool(wc, "endpointing"), gc.@endpointing, $"{label}.voiceCapabilities.endpointing");
            Equal(Bool(wc, "contextBias"), gc.@contextBias, $"{label}.voiceCapabilities.contextBias");
            Equal(Bool(wc, "languageBias"), gc.@languageBias, $"{label}.voiceCapabilities.languageBias");
            Equal(Bool(wc, "turnTimestamps"), gc.@turnTimestamps, $"{label}.voiceCapabilities.turnTimestamps");
            Equal(Bool(wc, "diarization"), gc.@diarization, $"{label}.voiceCapabilities.diarization");
            Equal(Bool(wc, "wordTimestamps"), gc.@wordTimestamps, $"{label}.voiceCapabilities.wordTimestamps");
        }

        // Resolved support, not the raw column: this pins the wildcard fallback
        // and the "uncatalogued ⇒ every language" rule as well.
        var kind = got.@kind == "text" ? HwKind.Text : HwKind.Voice;
        var support = HyperwhisperCoreMethods.ModelsLanguageSupport(got.@provider, kind, got.@id);
        Equal(Bool(want, "supportsAllLanguages"), support.@supportsAll, $"{label}.supportsAllLanguages");
        SequenceEqual(Strings(want, "languageCodes"), support.@codes, $"{label}.languageCodes");

        // The single-row lookup must agree with the bulk one.
        var single = HyperwhisperCoreMethods.ModelsEntry(got.@provider, kind, got.@id);
        True(single is not null, $"{label}: models_entry returned null for a catalogued row");
        Equal(got.@id, single!.@id, $"{label}: models_entry resolved a different row");
    }
}

void CheckCoverage()
{
    var entries = root.GetProperty("cloudSttEntries").EnumerateArray().ToArray();
    True(entries.Length >= 10, "expected the full provider list in the vectors");
    True(entries.Any(e => !Bool(e, "languagesHasCodes")!.Value),
        "no vector exercises the \"unverified\" languages.codes branch");
    True(entries.Any(e => Bool(e, "languagesHasCodes")!.Value),
        "no vector exercises the enumerated languages.codes branch");
    True(entries.Any(e => Num(e, "maxFileSizeMb") is null),
        "no vector exercises the \"unverified\" maxFileSizeMb branch");
    True(entries.Any(e => Int(e, "maxDurationMinutes") is not null),
        "no vector exercises the numeric maxDurationMinutes branch");
    True(root.GetProperty("vendorGroups").EnumerateArray()
            .Any(g => Strings(g, "entryIds").Count > 1),
        "no company owns two tiers, so the vendor merge is untested");
}

/// <summary>
/// The tri-state as the vectors spell it. Null means the catalog row carries no
/// <c>customVocabulary</c> block at all, which is not the same as an explicit no.
/// </summary>
static string? VocabName(VocabSupport? supported) => supported switch
{
    VocabSupport.Yes => "yes",
    VocabSupport.No => "no",
    VocabSupport.Unverified => "unverified",
    _ => null,
};

// --- vector readers ---------------------------------------------------------

static string? Str(JsonElement e, string name)
{
    var value = e.GetProperty(name);
    return value.ValueKind == JsonValueKind.Null ? null : value.GetString();
}

static bool? Bool(JsonElement e, string name)
{
    var value = e.GetProperty(name);
    return value.ValueKind == JsonValueKind.Null ? null : value.GetBoolean();
}

static double? Num(JsonElement e, string name)
{
    var value = e.GetProperty(name);
    return value.ValueKind == JsonValueKind.Null ? null : value.GetDouble();
}

static long? Int(JsonElement e, string name)
{
    var value = e.GetProperty(name);
    return value.ValueKind == JsonValueKind.Null ? null : value.GetInt64();
}

static List<string> Strings(JsonElement e, string name)
{
    var value = e.GetProperty(name);
    return value.ValueKind == JsonValueKind.Null
        ? []
        : [.. value.EnumerateArray().Select(item => item.GetString() ?? "")];
}

// The catalog has no live-only field, so each head keeps its own literal copy of
// the WEBSOCKET-ONLY model ids. That is drift waiting to happen, and it already
// happened once: Windows shipped with only `gemini-3.5-transcribe-live` while
// macOS and shared-.NET carried `gpt-live-transcribe` too, leaving an OpenAI
// model selectable in the Windows dictation picker on which every request 400s
// upstream at 17 credits/min. This check reads the OTHER heads' source as text,
// which is the only way a Linux-runnable test can catch a Swift or WPF literal.
void CheckLiveOnlyModelIds()
{
    var vectorPath = Path.Combine(AppContext.BaseDirectory, "live-only-models.json");
    if (!File.Exists(vectorPath))
        throw new FileNotFoundException($"live-only-models.json not found at {vectorPath}", vectorPath);

    using var vectors = JsonDocument.Parse(File.ReadAllText(vectorPath));
    var liveOnly = Strings(vectors.RootElement, "liveOnlyModelIds");
    var notLiveOnly = Strings(vectors.RootElement, "notLiveOnly");
    True(liveOnly.Count > 0, "the live-only vector lists at least one id");

    // 1. The shared-.NET copy — the one Windows and Linux both route through.
    SequenceEqual(
        [.. liveOnly.OrderBy(id => id, StringComparer.Ordinal)],
        [.. SharedCoreBridge.LiveOnlyCloudSttModelIds.OrderBy(id => id, StringComparer.Ordinal)],
        "SharedCoreBridge.LiveOnlyCloudSttModelIds");

    foreach (var id in liveOnly)
    {
        True(SharedCoreBridge.IsLiveOnlyCloudSttModel(id), $"`{id}` reads as live-only");
        True(SharedCoreBridge.IsLiveOnlyCloudSttModel($"  {id.ToUpperInvariant()}  "),
            $"`{id}` reads as live-only when padded and upper-cased");
    }
    foreach (var id in notLiveOnly)
        True(!SharedCoreBridge.IsLiveOnlyCloudSttModel(id), $"`{id}` does NOT read as live-only");

    // 2. The Windows and macOS literals, checked as source text.
    var repoRoot = new DirectoryInfo(AppContext.BaseDirectory);
    while (repoRoot is not null && !Directory.Exists(Path.Combine(repoRoot.FullName, "shared-conformance")))
        repoRoot = repoRoot.Parent;
    True(repoRoot is not null, "the repo root is locatable from the test output directory");

    (string Path, string Symbol)[] mirrors =
    [
        ("app/windows/HyperWhisper/Services/AppClassification/CloudSttCatalog.cs", "LiveOnlyModelIds"),
        ("app/macos/hyperwhisper/Utilities/AppClassification/CloudSTTCatalog.swift", "liveOnlyModelIds"),
    ];

    foreach (var (relative, symbol) in mirrors)
    {
        var full = Path.Combine(repoRoot!.FullName, relative);
        True(File.Exists(full), $"the {symbol} mirror exists at {relative}");
        var source = File.ReadAllText(full);
        var declaration = source.IndexOf(symbol, StringComparison.Ordinal);
        True(declaration >= 0, $"{relative} declares {symbol}");

        // Read to the end of the literal's bracket, so an id merely mentioned in
        // a nearby comment cannot satisfy the check.
        var open = source.IndexOf('[', declaration);
        var close = open >= 0 ? source.IndexOf(']', open) : -1;
        True(open >= 0 && close > open, $"{relative}'s {symbol} has a bracketed literal");
        var literal = source[open..close];

        foreach (var id in liveOnly)
            True(literal.Contains($"\"{id}\"", StringComparison.Ordinal),
                $"{relative}'s {symbol} is missing `{id}` — the heads have drifted apart");
        foreach (var id in notLiveOnly)
            True(!literal.Contains($"\"{id}\"", StringComparison.Ordinal),
                $"{relative}'s {symbol} wrongly lists `{id}`, which is a pre-recorded model");
    }
}

// --- assertions -------------------------------------------------------------

static void True(bool condition, string what)
{
    if (!condition) throw new InvalidOperationException($"Catalog conformance failed: {what}");
}

static void Equal<T>(T? expected, T? actual, string what)
{
    if (!EqualityComparer<T?>.Default.Equals(expected, actual))
        throw new InvalidOperationException(
            $"Catalog conformance failed: {what} — expected `{expected?.ToString() ?? "null"}`, "
            + $"got `{actual?.ToString() ?? "null"}`");
}

static void SequenceEqual(IReadOnlyList<string> expected, IReadOnlyList<string> actual, string what)
{
    if (expected.Count != actual.Count || !expected.SequenceEqual(actual))
        throw new InvalidOperationException(
            $"Catalog conformance failed: {what} — expected [{string.Join(", ", expected)}], "
            + $"got [{string.Join(", ", actual)}]");
}
