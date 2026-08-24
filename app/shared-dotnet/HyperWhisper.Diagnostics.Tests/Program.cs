using System.IO.Compression;
using System.Text;
using HyperWhisper.Diagnostics;

var tests = new (string Name, Func<Task> Run)[]
{
    ("logger emits only fixed fields", LoggerUsesFixedFields),
    ("logger rotates within exact bound", LoggerRotates),
    ("single-file rotation keeps one file", SingleFileRotation),
    ("logger creates owner-only files", LoggerPermissions),
    ("cancelled log write is stable", CancelledLogWrite),
    ("disposed logger failure is stable", DisposedLogger),
    ("concurrent writes remain valid", ConcurrentWrites),
    ("archive has exact allowlisted entries", ExactArchiveEntries),
    ("archive excludes hostile privacy markers", PrivacyMarkersAbsent),
    ("archive discards malformed and oversized log lines", MalformedLogsDiscarded),
    ("archive creates owner-only file", ArchivePermissions),
    ("cancelled export is stable and atomic", CancelledExport),
    ("invalid destination is stable", InvalidDestination),
    ("unwritable destination is stable", UnwritableDestination),
};

foreach (var test in tests)
{
    await test.Run();
    Console.WriteLine($"PASS {test.Name}");
}

static async Task LoggerUsesFixedFields()
{
    using var temp = new TempDirectory();
    using var logger = new PrivacySafeRotatingLogger(temp.Path);
    var result = await logger.WriteAsync(new(DateTimeOffset.UnixEpoch, DiagnosticSeverity.Error, DiagnosticComponent.Audio, DiagnosticOutcome.Failed));
    Assert.True(result.Success);
    var text = await File.ReadAllTextAsync(System.IO.Path.Combine(temp.Path, "diagnostics.log"));
    Assert.Contains("Audio", text);
    Assert.DoesNotContain("message", text);
    Assert.True(Encoding.UTF8.GetByteCount(text) <= 256 * 1024);
}

static async Task LoggerRotates()
{
    using var temp = new TempDirectory();
    using var logger = new PrivacySafeRotatingLogger(temp.Path, maxFileBytes: 256, maxFiles: 3);
    for (var index = 0; index < 30; index++)
        Assert.True((await logger.WriteAsync(new(DateTimeOffset.UnixEpoch.AddSeconds(index), DiagnosticSeverity.Information,
            DiagnosticComponent.Application, DiagnosticOutcome.Succeeded))).Success);
    var files = Directory.GetFiles(temp.Path).Select(System.IO.Path.GetFileName).Order().ToArray();
    Assert.SequenceEqual(new[] { "diagnostics.log", "diagnostics.log.1", "diagnostics.log.2" }, files);
    Assert.True(files.All(name => new FileInfo(System.IO.Path.Combine(temp.Path, name!)).Length <= 256));
}

static async Task SingleFileRotation()
{
    using var temp = new TempDirectory();
    using var logger = new PrivacySafeRotatingLogger(temp.Path, maxFileBytes: 256, maxFiles: 1);
    for (var index = 0; index < 10; index++)
        await logger.WriteAsync(new(DateTimeOffset.UnixEpoch.AddSeconds(index), DiagnosticSeverity.Information,
            DiagnosticComponent.Application, DiagnosticOutcome.Succeeded));
    Assert.SequenceEqual(new[] { "diagnostics.log" }, Directory.GetFiles(temp.Path).Select(System.IO.Path.GetFileName));
    Assert.True(new FileInfo(System.IO.Path.Combine(temp.Path, "diagnostics.log")).Length <= 256);
}

static async Task LoggerPermissions()
{
    if (!OperatingSystem.IsLinux()) return;
    using var temp = new TempDirectory();
    using var logger = new PrivacySafeRotatingLogger(System.IO.Path.Combine(temp.Path, "logs"));
    await logger.WriteAsync(new(DateTimeOffset.UnixEpoch, DiagnosticSeverity.Warning, DiagnosticComponent.Portal, DiagnosticOutcome.Degraded));
    Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
        File.GetUnixFileMode(System.IO.Path.Combine(temp.Path, "logs", "diagnostics.log")));
    Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
        File.GetUnixFileMode(System.IO.Path.Combine(temp.Path, "logs")));
}

static async Task CancelledLogWrite()
{
    using var temp = new TempDirectory();
    using var logger = new PrivacySafeRotatingLogger(temp.Path);
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    var result = await logger.WriteAsync(new(DateTimeOffset.UtcNow, DiagnosticSeverity.Information,
        DiagnosticComponent.Application, DiagnosticOutcome.Started), cancellation.Token);
    Assert.Equal(DiagnosticFailure.Cancelled, result.Failure);
}

static async Task DisposedLogger()
{
    using var temp = new TempDirectory();
    var logger = new PrivacySafeRotatingLogger(temp.Path);
    logger.Dispose();
    var result = await logger.WriteAsync(new(DateTimeOffset.UtcNow, DiagnosticSeverity.Information,
        DiagnosticComponent.Application, DiagnosticOutcome.Started));
    Assert.Equal(DiagnosticFailure.LogUnavailable, result.Failure);
}

static async Task ConcurrentWrites()
{
    using var temp = new TempDirectory();
    using var logger = new PrivacySafeRotatingLogger(temp.Path, maxFileBytes: 64 * 1024, maxFiles: 2);
    var writes = Enumerable.Range(0, 32).Select(index => logger.WriteAsync(new(
        DateTimeOffset.UnixEpoch.AddSeconds(index), DiagnosticSeverity.Information,
        DiagnosticComponent.Transcription, DiagnosticOutcome.Succeeded)));
    var results = await Task.WhenAll(writes);
    Assert.True(results.All(result => result.Success));
    var lines = await File.ReadAllLinesAsync(System.IO.Path.Combine(temp.Path, "diagnostics.log"));
    Assert.Equal(32, lines.Length);
    Assert.True(lines.All(line => line.StartsWith('{') && line.EndsWith('}')));
}

static async Task ExactArchiveEntries()
{
    using var fixture = await Fixture.Create();
    var entries = fixture.Archive.Entries.Select(entry => entry.FullName).Order().ToArray();
    Assert.SequenceEqual(new[] { "capabilities.json", "logs/events.jsonl", "system.json" }, entries);
}

static async Task PrivacyMarkersAbsent()
{
    using var temp = new TempDirectory();
    var markers = new[] { "SECRET-KEY-987", "private spoken transcript", "clipboard payload", "support-user@example.invalid", "/home/private-user/voice.wav" };
    var hostile = string.Join('|', markers);
    var injectedValidEvent = "{\"timestampUtc\":\"1970-01-01T00:00:00+00:00\",\"severity\":\"Error\",\"component\":\"Audio\",\"outcome\":\"Failed\",\"message\":\"" + hostile + "\"}";
    await File.WriteAllTextAsync(System.IO.Path.Combine(temp.Path, "diagnostics.log"), hostile + "\n" + injectedValidEvent + "\n");
    var exporter = new DiagnosticArchiveExporter(temp.Path);
    var target = System.IO.Path.Combine(temp.Path, "support.zip");
    var result = await exporter.ExportAsync(target,
        DiagnosticSystemInfo.Create(hostile, hostile, hostile, hostile, hostile, hostile, hostile),
        new(true, true, true, true, true, true, true));
    Assert.True(result.Success);
    var bytes = await File.ReadAllBytesAsync(target);
    var raw = Encoding.Latin1.GetString(bytes);
    foreach (var marker in markers) Assert.DoesNotContain(marker, raw);
    using var archive = ZipFile.OpenRead(target);
    foreach (var entry in archive.Entries)
    {
        using var reader = new StreamReader(entry.Open());
        var content = await reader.ReadToEndAsync();
        foreach (var marker in markers) Assert.DoesNotContain(marker, content);
    }
}

static async Task MalformedLogsDiscarded()
{
    using var temp = new TempDirectory();
    await File.WriteAllTextAsync(System.IO.Path.Combine(temp.Path, "diagnostics.log"),
        "not json SECRET\n" + new string('x', 600) + "\n");
    var target = System.IO.Path.Combine(temp.Path, "support.zip");
    Assert.True((await new DiagnosticArchiveExporter(temp.Path).ExportAsync(target, TestData.SafeSystem(), new(false, false, false, false, false, false, false))).Success);
    using var archive = ZipFile.OpenRead(target);
    using var reader = new StreamReader(archive.GetEntry("logs/events.jsonl")!.Open());
    Assert.Equal(string.Empty, await reader.ReadToEndAsync());
}

static async Task ArchivePermissions()
{
    if (!OperatingSystem.IsLinux()) return;
    using var fixture = await Fixture.Create();
    Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(fixture.Target));
}

static async Task CancelledExport()
{
    using var temp = new TempDirectory();
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    var target = System.IO.Path.Combine(temp.Path, "cancelled.zip");
    var result = await new DiagnosticArchiveExporter(temp.Path).ExportAsync(target, TestData.SafeSystem(),
        new(false, false, false, false, false, false, false), cancellation.Token);
    Assert.Equal(DiagnosticFailure.Cancelled, result.Failure);
    Assert.False(File.Exists(target));
    Assert.False(Directory.GetFiles(temp.Path, "*.tmp").Any());
}

static async Task InvalidDestination()
{
    using var temp = new TempDirectory();
    var result = await new DiagnosticArchiveExporter(temp.Path).ExportAsync(System.IO.Path.Combine(temp.Path, "not-zip.txt"),
        TestData.SafeSystem(), new(false, false, false, false, false, false, false));
    Assert.Equal(DiagnosticFailure.InvalidDestination, result.Failure);
}

static async Task UnwritableDestination()
{
    using var temp = new TempDirectory();
    var blockingFile = System.IO.Path.Combine(temp.Path, "file");
    await File.WriteAllTextAsync(blockingFile, "x");
    var result = await new DiagnosticArchiveExporter(temp.Path).ExportAsync(System.IO.Path.Combine(blockingFile, "support.zip"),
        TestData.SafeSystem(), new(false, false, false, false, false, false, false));
    Assert.Equal(DiagnosticFailure.DestinationUnavailable, result.Failure);
}

sealed class Fixture : IDisposable
{
    private readonly TempDirectory _temp;
    public string Target { get; }
    public ZipArchive Archive { get; }
    private Fixture(TempDirectory temp, string target, ZipArchive archive) => (_temp, Target, Archive) = (temp, target, archive);
    public static async Task<Fixture> Create()
    {
        var temp = new TempDirectory();
        using (var logger = new PrivacySafeRotatingLogger(temp.Path))
            await logger.WriteAsync(new(DateTimeOffset.UnixEpoch, DiagnosticSeverity.Information, DiagnosticComponent.Application, DiagnosticOutcome.Started));
        var target = System.IO.Path.Combine(temp.Path, "support.zip");
        var result = await new DiagnosticArchiveExporter(temp.Path).ExportAsync(target, TestData.SafeSystem(), new(true, false, true, false, true, false, false));
        Assert.True(result.Success);
        return new Fixture(temp, target, ZipFile.OpenRead(target));
    }
    public void Dispose() { Archive.Dispose(); _temp.Dispose(); }
}

static class TestData
{
    public static DiagnosticSystemInfo SafeSystem() => DiagnosticSystemInfo.Create(
        "1.2.3", "Linux", "Debian 12", "Linux 6.1", "X64", "GNOME", "wayland");
}

sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hyperwhisper-diagnostics-tests", Guid.NewGuid().ToString("N"));
    public TempDirectory() => Directory.CreateDirectory(Path);
    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}

static class Assert
{
    public static void True(bool value) { if (!value) throw new InvalidOperationException("Expected true."); }
    public static void False(bool value) => True(!value);
    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}; got {actual}.");
    }
    public static void Contains(string expected, string actual) { if (!actual.Contains(expected, StringComparison.Ordinal)) throw new InvalidOperationException($"Missing {expected}."); }
    public static void DoesNotContain(string expected, string actual) { if (actual.Contains(expected, StringComparison.Ordinal)) throw new InvalidOperationException($"Unexpected {expected}."); }
    public static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual)
    {
        if (!expected.SequenceEqual(actual)) throw new InvalidOperationException("Sequences differ.");
    }
}
