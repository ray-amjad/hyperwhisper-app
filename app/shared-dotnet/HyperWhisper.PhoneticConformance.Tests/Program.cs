// Runs shared-conformance/phonetic-vectors.json against the C# UniFFI binding
// and the SharedCoreBridge facade built on it.
//
// Issue #283 deleted the native phonetic matchers, so there is exactly one
// matcher and exactly one diacritic-insensitive substring pass. These vectors
// prove the .NET heads read that one implementation's answer unchanged. Rust
// and Swift run the same file:
//
//   shared-core-rs/crates/hw-core/tests/phonetic_vectors.rs
//   app/macos/hyperwhisperTests/PhoneticConformanceVectorTests.swift
//
// `matcherCases` carries a `decision` field naming which native matcher each
// unified answer came from. The rows that say `windows`, `macos`, `neither` or
// `new` are the behaviour changes.
//
// `substringCases` has no `decision` field: only macOS ever had that pass, so
// every row is the macOS behaviour. Windows and Linux gain it in this change,
// and these rows are what keeps it from drifting.
//
// Regenerate the vectors from Rust after an intended policy change:
//   cd shared-core-rs && cargo test -p hw-core --test phonetic_vectors -- --ignored regenerate

using System.Text.Json;
using HyperWhisper.SharedCore;

var path = Path.Combine(AppContext.BaseDirectory, "phonetic-vectors.json");
if (!File.Exists(path))
    throw new FileNotFoundException($"phonetic-vectors.json not found at {path}", path);

using var document = JsonDocument.Parse(File.ReadAllText(path));
var root = document.RootElement;

var checks = new (string Name, Action Run)[]
{
    ("the phonetic matcher matches the vectors", CheckMatcher),
    ("the substring pass matches the vectors", CheckSubstring),
    ("a null or empty entry list leaves the text alone", CheckEmptyEntries),
    ("every changed-behaviour bucket still has a row", CheckDecisionBuckets),
};

foreach (var check in checks)
{
    check.Run();
    Console.WriteLine($"PASS {check.Name}");
}
Console.WriteLine($"Phonetic conformance: {checks.Length}/{checks.Length} checks passed.");

// ---------------------------------------------------------------------------

void CheckMatcher()
{
    foreach (var vector in MatcherCases())
    {
        var name = Str(vector, "name")!;
        var expected = vector.GetProperty("expected");
        var actual = SharedCoreBridge.ApplyPhoneticVocabulary(
            Str(vector, "text") ?? "", Entries(vector));

        Equal(Str(expected, "text"), actual.Text, $"{name}: text");
        Equal(expected.GetProperty("entryCount").GetUInt32(), actual.EntryCount, $"{name}: entryCount");

        var wanted = expected.GetProperty("matches").EnumerateArray().ToArray();
        Equal(wanted.Length, actual.Matches.Count, $"{name}: match count");
        for (var index = 0; index < wanted.Length; index++)
        {
            Equal(Str(wanted[index], "token"), actual.Matches[index].Token, $"{name}: token {index}");
            Equal(
                Str(wanted[index], "replacement"),
                actual.Matches[index].Replacement,
                $"{name}: replacement {index}");
        }
    }
}

void CheckSubstring()
{
    foreach (var vector in SubstringCases())
    {
        var name = Str(vector, "name")!;
        var actual = SharedCoreBridge.ApplySubstringVocabulary(
            Str(vector, "text") ?? "", Entries(vector));
        Equal(Str(vector, "expected"), actual, name);
    }
}

// Both call sites hand the bridge whatever the vocabulary store returns, which
// is empty for most users. The facade has to survive that without a round-trip
// to the core changing the text.
void CheckEmptyEntries()
{
    foreach (var entries in new IReadOnlyList<PortableVocabularyEntry>?[] { null, [] })
    {
        Equal("nothing to correct", SharedCoreBridge.ApplySubstringVocabulary("nothing to correct", entries),
            "substring with no entries");

        var phonetic = SharedCoreBridge.ApplyPhoneticVocabulary("nothing to correct", entries);
        Equal("nothing to correct", phonetic.Text, "matcher with no entries");
        Equal(0u, phonetic.EntryCount, "matcher entryCount with no entries");
        Equal(0, phonetic.Matches.Count, "matcher match count with no entries");
    }
}

// A decision table is only proof while it still has a row in every bucket that
// records a behaviour change. Mirrors the Rust and Swift runners.
void CheckDecisionBuckets()
{
    var vectors = MatcherCases().ToArray();
    True(vectors.Length >= 10, "expected the full matcher vector set");
    True(SubstringCases().Any(), "expected at least one substring vector");

    foreach (var decision in new[] { "agreed", "windows", "macos", "neither", "new" })
        True(
            vectors.Any(vector => Str(vector, "decision") == decision),
            $"decision bucket {decision} lost its last vector");
}

IEnumerable<JsonElement> MatcherCases() => root.GetProperty("matcherCases").EnumerateArray();

IEnumerable<JsonElement> SubstringCases() => root.GetProperty("substringCases").EnumerateArray();

// --- vector readers ---------------------------------------------------------

static List<PortableVocabularyEntry> Entries(JsonElement vector) =>
[
    .. vector.GetProperty("entries").EnumerateArray()
        .Select(entry => new PortableVocabularyEntry(
            Str(entry, "word") ?? "", Str(entry, "replacement")))
];

static string? Str(JsonElement e, string name)
{
    var value = e.GetProperty(name);
    return value.ValueKind == JsonValueKind.Null ? null : value.GetString();
}

// --- assertions -------------------------------------------------------------

static void True(bool condition, string what)
{
    if (!condition) throw new InvalidOperationException($"Phonetic conformance failed: {what}");
}

static void Equal<T>(T? expected, T? actual, string what)
{
    if (!EqualityComparer<T?>.Default.Equals(expected, actual))
        throw new InvalidOperationException(
            $"Phonetic conformance failed: {what} — expected `{expected?.ToString() ?? "null"}`, "
            + $"got `{actual?.ToString() ?? "null"}`");
}
