// Runs shared-conformance/language-vectors.json against the C# UniFFI binding
// and the SharedCoreBridge facade built on it.
//
// Issue #285 moved the language catalog into hw-catalog and deleted the native
// tables, so there is exactly one list of languages and exactly one spelling
// rule for a code. These vectors prove the .NET heads read that one catalog's
// answer unchanged. Rust and Swift run the same file:
//
//   shared-core-rs/crates/hw-core/tests/language_vectors.rs
//   app/macos/hyperwhisperTests/LanguageConformanceVectorTests.swift
//
// Every catalog row carries a `decision` field naming where the unified row came
// from. The 24 rows that say `macos` are exactly what the WINDOWS picker gains —
// the region and script variants (en-GB, pt-BR, zh-Hant, …) it never listed. The
// one `renamed` row is the only string a user reads differently than before.
//
// What the harness can and cannot reach. `HyperWhisper.Models.LanguageInfo` —
// the Windows facade that is the real thing under test for the picker — lives in
// the WPF assembly, which does not build on Linux, so this harness cannot
// reference it. It asserts SharedCoreBridge instead, which is every line of
// behaviour that facade now has: its AllLanguages is the bridge's list, and its
// GetDisplayName is canonicalize-then-look-up, which is checked here by
// `lookupCases`.
//
// Regenerate the vectors from Rust after an intended catalog change:
//   cd shared-core-rs && cargo test -p hw-core --test language_vectors -- --ignored regenerate

using System.Text.Json;
using HyperWhisper.SharedCore;

var path = Path.Combine(AppContext.BaseDirectory, "language-vectors.json");
if (!File.Exists(path))
    throw new FileNotFoundException($"language-vectors.json not found at {path}", path);

using var document = JsonDocument.Parse(File.ReadAllText(path));
var root = document.RootElement;

var checks = new (string Name, Action Run)[]
{
    ("the whole catalog matches, row for row, in picker order", CheckCatalog),
    ("every canonicalization case matches", CheckCanonicalCases),
    ("every lookup case matches", CheckLookupCases),
    ("every scalar case matches", CheckScalarCases),
    ("the popular codes match, in picker order", CheckPopularCodes),
    ("the 24 rows the Windows picker gains are all present", CheckWindowsGains),
    ("the two Traditional Chinese rows read differently, and every name is unique", CheckDisplayNames),
    ("an uncatalogued code comes back canonical with no name", CheckUnknownCodes),
    ("a provider's advertised list resolves with automatic first", CheckResolveAndPrioritize),
};

foreach (var check in checks)
{
    check.Run();
    Console.WriteLine($"PASS {check.Name}");
}
Console.WriteLine($"Language catalog conformance: {checks.Length}/{checks.Length} checks passed.");

// ---------------------------------------------------------------------------

void CheckCatalog()
{
    var expected = Catalog().ToList();
    var actual = SharedCoreBridge.AllLanguages();

    Equal(expected.Count, actual.Count, "catalog row count");

    for (var index = 0; index < expected.Count; index++)
    {
        var code = Str(expected[index], "code")!;
        Equal(code, actual[index].Code, $"catalog row {index} code");
        Equal(
            Str(expected[index], "displayName"),
            actual[index].DisplayName,
            $"catalog row {index} ({code}) displayName");
        True(
            Str(expected[index], "decision") is "both" or "macos" or "renamed",
            $"catalog row {index} ({code}) has an unknown decision {Str(expected[index], "decision")}");
    }
}

void CheckCanonicalCases()
{
    foreach (var vector in Rows("canonicalCases"))
    {
        var name = Str(vector, "name")!;
        Equal(
            Str(vector, "canonical"),
            SharedCoreBridge.CanonicalizeLanguageCode(Str(vector, "input")!),
            $"{name}: canonicalize {Quoted(Str(vector, "input"))}");
    }
}

void CheckLookupCases()
{
    foreach (var vector in Rows("lookupCases"))
    {
        var name = Str(vector, "name")!;
        var input = Str(vector, "input")!;
        var expectedName = Str(vector, "displayName");

        // The catalog is asked for the RAW input: canonicalizing first is the
        // core's job, and a host that had to do it would be a second spelling
        // rule. This is the exact call HyperWhisper.Models.LanguageInfo
        // .GetDisplayName makes, and the reason a stored `en_GB` now resolves.
        var actual = SharedCoreBridge.LanguageInfo(input);

        if (expectedName is null)
        {
            True(actual is null, $"{name}: {Quoted(input)} should not be in the catalog");
            continue;
        }

        True(actual is not null, $"{name}: {Quoted(input)} should be in the catalog");
        Equal(Str(vector, "code"), actual!.Code, $"{name}: {Quoted(input)} code");
        Equal(expectedName, actual.DisplayName, $"{name}: {Quoted(input)} displayName");
    }
}

void CheckScalarCases()
{
    foreach (var vector in Rows("scalarCases"))
    {
        var name = Str(vector, "name")!;
        // A JSON null input is a genuinely absent code, which is a different
        // case from an empty string: it normalizes to `en`, not to `auto`.
        var input = Str(vector, "input");

        Equal(
            Str(vector, "normalized"),
            SharedCoreBridge.NormalizeLanguageCode(input),
            $"{name}: normalize {Quoted(input)}");
        Equal(
            Str(vector, "canonicalCode"),
            SharedCoreBridge.CanonicalLanguageCode(input),
            $"{name}: canonical code for {Quoted(input)}");
        Equal(
            vector.GetProperty("isEnglish").GetBoolean(),
            SharedCoreBridge.IsEnglishLanguage(input),
            $"{name}: isEnglish for {Quoted(input)}");
    }
}

void CheckPopularCodes()
{
    var expected = root.GetProperty("popularCodes").EnumerateArray()
        .Select(code => code.GetString()).ToList();
    var actual = SharedCoreBridge.PopularLanguageCodes();

    Equal(expected.Count, actual.Count, "popular code count");
    for (var index = 0; index < expected.Count; index++)
        Equal(expected[index], actual[index], $"popular code {index}");

    // The picker order the whole list is built in: `auto`, then these, then the
    // alphabetical remainder. Proving the prefix here is what makes the
    // full-catalog check above a check of ORDER and not just of membership.
    var all = SharedCoreBridge.AllLanguages();
    Equal("auto", all[0].Code, "the first picker row is automatic");
    for (var index = 0; index < expected.Count; index++)
        Equal(expected[index], all[index + 1].Code, $"picker row {index + 1} is popular code {index}");
}

void CheckWindowsGains()
{
    var gained = Catalog().Where(row => Str(row, "decision") == "macos").ToList();

    // Hard-coded because it is the claim the class doc on
    // HyperWhisper.Models.LanguageInfo and the Windows CI step both make. If the
    // catalog grows, both of those sentences need rewriting too.
    Equal(24, gained.Count, "rows the Windows picker gains");

    var catalogued = SharedCoreBridge.AllLanguages().Select(language => language.Code).ToHashSet(StringComparer.Ordinal);
    foreach (var row in gained)
    {
        var code = Str(row, "code")!;
        True(catalogued.Contains(code), $"{code} is a row the Windows picker gains, and it is missing");
        True(
            SharedCoreBridge.LanguageInfo(code) is not null,
            $"{code} is in the picker list but does not resolve on its own");
    }
}

void CheckDisplayNames()
{
    // The one `renamed` row, and the row it is now told apart from. Before
    // #285 both platforms called zh-TW "Chinese (Traditional)", which left no
    // name at all for the script-only tag.
    Equal(
        "Chinese (Traditional, Taiwan)",
        SharedCoreBridge.LanguageInfo("zh-TW")?.DisplayName,
        "zh-TW display name");
    Equal(
        "Chinese (Traditional)",
        SharedCoreBridge.LanguageInfo("zh-Hant")?.DisplayName,
        "zh-Hant display name");

    // Two picker rows that read identically are indistinguishable to the user,
    // which is the defect the rename fixes. Assert it for the whole list.
    var seen = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var language in SharedCoreBridge.AllLanguages())
    {
        True(
            !string.IsNullOrWhiteSpace(language.DisplayName),
            $"catalog row {language.Code} has no display name");
        True(
            seen.TryAdd(language.DisplayName!, language.Code),
            $"display name {Quoted(language.DisplayName)} is on both {seen.GetValueOrDefault(language.DisplayName!)} and {language.Code}");
    }
}

void CheckUnknownCodes()
{
    foreach (var vector in Rows("lookupCases"))
    {
        if (Str(vector, "displayName") is not null) continue;

        var input = Str(vector, "input")!;
        var expectedCode = Str(vector, "code")!;

        True(SharedCoreBridge.LanguageInfo(input) is null, $"{Quoted(input)} should not be catalogued");
        // The host still gets a canonical tag to persist and to localize from,
        // which is the whole contract for a code the catalog does not know.
        Equal(expectedCode, SharedCoreBridge.CanonicalizeLanguageCode(input), $"canonical form of {Quoted(input)}");

        var resolved = SharedCoreBridge.ResolveLanguages([input]);
        Equal(1, resolved.Count, $"resolving {Quoted(input)} yields one row");
        Equal(expectedCode, resolved[0].Code, $"resolved code for {Quoted(input)}");
        Equal(null, resolved[0].DisplayName, $"resolved display name for {Quoted(input)}");
    }
}

void CheckResolveAndPrioritize()
{
    // A provider list in the shape the pickers actually meet one: mixed
    // spellings, a duplicate, an uncatalogued code, and `auto` not first.
    var resolved = SharedCoreBridge.ResolveLanguages(["en_GB", "zh-hant", "auto", "EN-GB", "zz"]);
    string[] expectedCodes = ["en-GB", "zh-Hant", "auto", "zz"];

    Equal(expectedCodes.Length, resolved.Count, "resolved row count after deduplication");
    for (var index = 0; index < expectedCodes.Length; index++)
        Equal(expectedCodes[index], resolved[index].Code, $"resolved row {index}");
    Equal("English (United Kingdom)", resolved[0].DisplayName, "resolved en-GB display name");
    Equal(null, resolved[3].DisplayName, "resolved zz display name");

    var prioritized = SharedCoreBridge.PrioritizeAutomaticLanguage(resolved);
    Equal(resolved.Count, prioritized.Count, "prioritizing keeps every row");
    Equal("auto", prioritized[0].Code, "automatic sorts first");
    string[] expectedAfter = ["auto", "en-GB", "zh-Hant", "zz"];
    for (var index = 0; index < expectedAfter.Length; index++)
        Equal(expectedAfter[index], prioritized[index].Code, $"prioritized row {index}");

    // Nothing to move, and nothing to crash on.
    Equal(0, SharedCoreBridge.ResolveLanguages(null).Count, "a null provider list resolves to nothing");
    Equal(0, SharedCoreBridge.PrioritizeAutomaticLanguage(null).Count, "a null list prioritizes to nothing");
}

// ---------------------------------------------------------------------------

IEnumerable<JsonElement> Catalog() => Rows("catalog");

IEnumerable<JsonElement> Rows(string property) => root.GetProperty(property).EnumerateArray();

static string? Str(JsonElement element, string property) =>
    element.TryGetProperty(property, out var value) ? value.GetString() : null;

static string Quoted(string? value) => value is null ? "null" : $"\"{value}\"";

static void Equal<T>(T? expected, T? actual, string what)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException(
            $"Language catalog conformance failed: {what}: expected {expected}, got {actual}");
}

static void True(bool condition, string what)
{
    if (!condition) throw new InvalidOperationException($"Language catalog conformance failed: {what}");
}
