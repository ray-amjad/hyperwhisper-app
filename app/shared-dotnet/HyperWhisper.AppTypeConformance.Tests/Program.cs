// Runs shared-conformance/app-type-vectors.json against the C# UniFFI binding
// and the platform facade built on it.
//
// Issue #279 deleted the native app-type classifiers, so there is exactly one
// implementation of the 8-element priority order, the title word-boundary rule,
// the host-suffix rule and the email regex. These vectors prove the .NET heads
// read that one implementation's answer unchanged. Rust and Swift run the same
// file:
//
//   shared-core-rs/crates/hw-core/tests/app_type_vectors.rs
//   app/macos/hyperwhisperTests/AppTypeConformanceVectorTests.swift
//
// Regenerate the vectors from Rust after an intended catalog change:
//   cd shared-core-rs && cargo test -p hw-core --test app_type_vectors -- --ignored regenerate

using System.Text.Json;
using HyperWhisper.AppClassification;

var path = Path.Combine(AppContext.BaseDirectory, "app-type-vectors.json");
if (!File.Exists(path))
    throw new FileNotFoundException($"app-type-vectors.json not found at {path}", path);

using var document = JsonDocument.Parse(File.ReadAllText(path));
var root = document.RootElement;

var checks = new (string Name, Action Run)[]
{
    ("classification matches the vectors", CheckClassifications),
    ("the derived AppType strings match the vectors", CheckDerivedStrings),
    ("webmail detection matches the vectors", CheckWebmail),
    ("the vectors cover every rule and signal", CheckCoverage),
};

foreach (var check in checks)
{
    check.Run();
    Console.WriteLine($"PASS {check.Name}");
}
Console.WriteLine($"App-type conformance: {checks.Length}/{checks.Length} checks passed.");

// ---------------------------------------------------------------------------

void CheckClassifications()
{
    foreach (var vector in Classifications())
    {
        var name = Str(vector, "name")!;
        var expected = vector.GetProperty("expected");
        var actual = AppTypeClassifier.Classify(RequestFrom(vector.GetProperty("request")));

        Equal(Str(expected, "appType"), actual.AppType.ToString(), $"{name}: appType");
        Equal(Str(expected, "confidence"), actual.Confidence, $"{name}: confidence");
        Equal(Str(expected, "source"), actual.Source, $"{name}: source");
        Equal(Str(expected, "matched"), actual.Matched, $"{name}: matched");
    }
}

// The `AppTypeExtensions` switches are the one piece of app-type behaviour the
// natives keep (the issue names six importing call sites on Windows alone).
// They are only safe to keep while they agree with the shared core, which
// resolves the same three strings on every classification.
void CheckDerivedStrings()
{
    foreach (var vector in Classifications())
    {
        var name = Str(vector, "name")!;
        var expected = vector.GetProperty("expected");
        var appType = AppTypeClassifier.Classify(RequestFrom(vector.GetProperty("request"))).AppType;

        Equal(Str(expected, "promptValue"), appType.ToPromptValue(), $"{name}: promptValue");
        Equal(Str(expected, "category"), appType.ToCategory(), $"{name}: category");
        Equal(Str(expected, "textInputFormat"), appType.ToTextFormat(), $"{name}: textInputFormat");
    }
}

void CheckWebmail()
{
    foreach (var vector in root.GetProperty("webmailTitles").EnumerateArray())
    {
        var title = Str(vector, "title") ?? "";
        Equal(
            vector.GetProperty("expected").GetBoolean(),
            AppTypeClassifier.IsWebmail(title),
            $"isWebmail({JsonSerializer.Serialize(title)})");
    }
}

// A vector file that stopped exercising a rule would pass every check above
// while proving nothing. This mirrors the Rust runner's own coverage test.
void CheckCoverage()
{
    var vectors = Classifications().ToArray();
    True(vectors.Length >= 40, "expected the full classification vector set");

    foreach (var rule in new[] { "priorityOrder", "wordBoundary", "hostSuffix", "emailRegex" })
        True(vectors.Count(v => Str(v, "rule") == rule) >= 4, $"rule {rule} has too few vectors");

    foreach (var source in new[]
    {
        "browserHost", "bundleId", "processName", "title",
        "appName", "focusedElement", "focusedElementText", "default"
    })
    {
        True(
            vectors.Any(v => Str(v.GetProperty("expected"), "source") == source),
            $"no vector reaches the {source} signal");
    }

    foreach (var appType in Enum.GetValues<AppType>())
    {
        True(
            vectors.Any(v => Str(v.GetProperty("expected"), "appType") == appType.ToString()),
            $"no vector classifies as {appType}");
    }

    var webmail = root.GetProperty("webmailTitles").EnumerateArray().ToArray();
    foreach (var branch in new[] { "keyword", "address", "none" })
        True(webmail.Any(w => Str(w, "branch") == branch), $"no webmail vector is a {branch} case");
    True(webmail.Any(w => w.GetProperty("expected").GetBoolean()), "no positive webmail vector");
    True(webmail.Any(w => !w.GetProperty("expected").GetBoolean()), "no negative webmail vector");
}

IEnumerable<JsonElement> Classifications() => root.GetProperty("classifications").EnumerateArray();

// --- vector readers ---------------------------------------------------------

static AppClassificationRequest RequestFrom(JsonElement e) => new(
    BundleId: Str(e, "bundleId") ?? "",
    ProcessName: Str(e, "processName") ?? "",
    AppName: Str(e, "appName") ?? "",
    Host: Str(e, "host"),
    HostConfidence: Str(e, "hostConfidence") ?? "",
    Title: Str(e, "title") ?? "",
    FocusedPieces: Strings(e, "focusedPieces"));

static string? Str(JsonElement e, string name)
{
    var value = e.GetProperty(name);
    return value.ValueKind == JsonValueKind.Null ? null : value.GetString();
}

static List<string> Strings(JsonElement e, string name)
{
    var value = e.GetProperty(name);
    return value.ValueKind == JsonValueKind.Null
        ? []
        : [.. value.EnumerateArray().Select(item => item.GetString() ?? "")];
}

// --- assertions -------------------------------------------------------------

static void True(bool condition, string what)
{
    if (!condition) throw new InvalidOperationException($"App-type conformance failed: {what}");
}

static void Equal<T>(T? expected, T? actual, string what)
{
    if (!EqualityComparer<T?>.Default.Equals(expected, actual))
        throw new InvalidOperationException(
            $"App-type conformance failed: {what} — expected `{expected?.ToString() ?? "null"}`, "
            + $"got `{actual?.ToString() ?? "null"}`");
}
