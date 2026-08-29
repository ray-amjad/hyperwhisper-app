// Runs shared-conformance/stats-vectors.json against the C# UniFFI binding and
// the SharedCoreBridge facade built on it.
//
// Issue #285 deleted the three native home-statistics implementations, so there
// is exactly one set of formulas. These vectors prove the .NET heads read that
// one implementation's answer unchanged. Rust and Swift run the same file:
//
//   shared-core-rs/crates/hw-core/tests/stats_vectors.rs
//   app/macos/hyperwhisperTests/StatsConformanceVectorTests.swift
//
// Every case carries a `decision` field naming which native copy the unified
// answer came from. The rows that say `macos`, `dotnet`, `neither` or `new` are
// the behaviour changes: macOS had no saved-minutes ceiling and no
// finite-duration guard, C# rounded to even, macOS started the week on Sunday,
// and one .NET copy forced the week and month boundaries to UTC.
//
// Two conventions the JSON needs. Every instant is LOCAL epoch seconds — the
// host shifts each row into the calendar time zone before it calls. A
// `durationSeconds` of null means the store held a value that is not a finite
// positive number; JSON cannot carry NaN, and the exact value does not matter
// because both normalise to zero.
//
// Regenerate the vectors from Rust after an intended policy change:
//   cd shared-core-rs && cargo test -p hw-core --test stats_vectors -- --ignored regenerate

using System.Text.Json;
using HyperWhisper.SharedCore;

var path = Path.Combine(AppContext.BaseDirectory, "stats-vectors.json");
if (!File.Exists(path))
    throw new FileNotFoundException($"stats-vectors.json not found at {path}", path);

using var document = JsonDocument.Parse(File.ReadAllText(path));
var root = document.RootElement;

var checks = new (string Name, Action Run)[]
{
    ("every case matches the shared core", CheckCases),
    ("an empty history is every figure at zero", CheckEmptyHistory),
    ("the saved-minutes ceiling is one week", CheckCeiling),
    ("every changed-behaviour bucket still has a row", CheckDecisionBuckets),
};

foreach (var check in checks)
{
    check.Run();
    Console.WriteLine($"PASS {check.Name}");
}
Console.WriteLine($"Home statistics conformance: {checks.Length}/{checks.Length} checks passed.");

// ---------------------------------------------------------------------------

void CheckCases()
{
    foreach (var vector in Cases())
    {
        var name = Str(vector, "name")!;
        var expected = vector.GetProperty("expected");
        var actual = SharedCoreBridge.CalculateHomeStatistics(
            Transcripts(vector),
            vector.GetProperty("typingSpeedWordsPerMinute").GetInt32(),
            vector.GetProperty("nowLocalEpochSeconds").GetInt64());

        Period(expected.GetProperty("thisWeek"), actual.ThisWeek, $"{name}: thisWeek");
        Period(expected.GetProperty("thisMonth"), actual.ThisMonth, $"{name}: thisMonth");
        Period(expected.GetProperty("thisYear"), actual.ThisYear, $"{name}: thisYear");
        Period(expected.GetProperty("allTime"), actual.AllTime, $"{name}: allTime");

        Equal(
            expected.GetProperty("typingSpeedWordsPerMinute").GetInt32(),
            actual.TypingSpeedWordsPerMinute,
            $"{name}: typingSpeedWordsPerMinute");
        Equal(
            expected.GetProperty("averageWordsPerMinute").GetInt32(),
            actual.AverageWordsPerMinute,
            $"{name}: averageWordsPerMinute");
        Equal(
            expected.GetProperty("savedThisWeekMinutes").GetInt32(),
            actual.SavedThisWeekMinutes,
            $"{name}: savedThisWeekMinutes");
    }
}

void CheckEmptyHistory()
{
    foreach (var rows in new IReadOnlyList<PortableStatsTranscript>?[] { null, [] })
    {
        var snapshot = SharedCoreBridge.CalculateHomeStatistics(rows, 40, 0);
        Equal(0, snapshot.AllTime.WordCount, "empty history word count");
        Equal(0, snapshot.AverageWordsPerMinute, "empty history average WPM");
        Equal(0, snapshot.SavedThisWeekMinutes, "empty history saved minutes");
        Equal(40, snapshot.TypingSpeedWordsPerMinute, "empty history echoes the typing speed");
    }
}

void CheckCeiling()
{
    Equal(7 * 24 * 60, SharedCoreBridge.SavedThisWeekMinutesCeiling, "saved-minutes ceiling");
}

void CheckDecisionBuckets()
{
    string[] known = ["agreed", "macos", "dotnet", "neither", "new"];
    string[] changed = ["macos", "dotnet", "neither", "new"];

    foreach (var vector in Cases())
    {
        var decision = Str(vector, "decision")!;
        True(known.Contains(decision), $"unknown decision label {decision}");
        if (decision != "agreed")
            True(
                !string.IsNullOrWhiteSpace(Str(vector, "was")),
                $"{Str(vector, "name")} is a {decision} row with no `was` note");
    }

    foreach (var decision in changed)
        True(
            Cases().Any(vector => Str(vector, "decision") == decision),
            $"no {decision} row left — that behaviour change is no longer proven");
}

// ---------------------------------------------------------------------------

IEnumerable<JsonElement> Cases() => root.GetProperty("cases").EnumerateArray();

List<PortableStatsTranscript> Transcripts(JsonElement vector) =>
    vector.GetProperty("transcripts").EnumerateArray()
        .Select(row => new PortableStatsTranscript(
            row.GetProperty("createdAtLocalEpochSeconds").GetInt64(),
            (int)row.GetProperty("wordCount").GetUInt32(),
            // null stands for any non-finite stored value; NaN is the one that
            // trapped on macOS, so it is the one the harness replays.
            row.GetProperty("durationSeconds").ValueKind == JsonValueKind.Null
                ? double.NaN
                : row.GetProperty("durationSeconds").GetDouble(),
            Str(row, "status") switch
            {
                "processing" => PortableStatsTranscriptStatus.Processing,
                "failed" => PortableStatsTranscriptStatus.Failed,
                "completed" => PortableStatsTranscriptStatus.Completed,
                var other => throw new InvalidOperationException($"unknown status {other}"),
            }))
        .ToList();

void Period(JsonElement expected, PortablePeriodStats actual, string what)
{
    // The vectors carry a word count that can saturate at uint.MaxValue, which
    // the bridge saturates again on the way down to int.
    var words = expected.GetProperty("wordCount").GetUInt32();
    Equal((int)Math.Min(words, int.MaxValue), actual.WordCount, $"{what}.wordCount");
    Nearly(
        expected.GetProperty("durationSeconds").GetDouble(),
        actual.DurationSeconds,
        $"{what}.durationSeconds");
    Equal(
        expected.GetProperty("averageWordsPerMinute").GetInt32(),
        actual.AverageWordsPerMinute,
        $"{what}.averageWordsPerMinute");
    Nearly(
        expected.GetProperty("estimatedTypingMinutes").GetDouble(),
        actual.EstimatedTypingMinutes,
        $"{what}.estimatedTypingMinutes");
    Nearly(
        expected.GetProperty("estimatedTimeSavedMinutes").GetDouble(),
        actual.EstimatedTimeSavedMinutes,
        $"{what}.estimatedTimeSavedMinutes");
}

static string? Str(JsonElement element, string property) =>
    element.TryGetProperty(property, out var value) ? value.GetString() : null;

static void Equal<T>(T? expected, T? actual, string what)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException(
            $"Home statistics conformance failed: {what}: expected {expected}, got {actual}");
}

// The vectors are generated from f64 and read back through System.Text.Json, so
// compare with a tolerance rather than for bit equality.
static void Nearly(double expected, double actual, string what)
{
    var tolerance = Math.Max(1e-9, Math.Abs(expected) * 1e-12);
    if (Math.Abs(expected - actual) > tolerance)
        throw new InvalidOperationException(
            $"Home statistics conformance failed: {what}: expected {expected}, got {actual}");
}

static void True(bool condition, string what)
{
    if (!condition) throw new InvalidOperationException($"Home statistics conformance failed: {what}");
}
