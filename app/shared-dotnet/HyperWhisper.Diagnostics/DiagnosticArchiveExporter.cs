using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace HyperWhisper.Diagnostics;

public sealed class DiagnosticArchiveExporter
{
    public const int MaximumArchiveBytes = 2 * 1024 * 1024;
    private const int MaximumLogInputBytes = 1024 * 1024;
    private static readonly DateTimeOffset StableZipTimestamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly string _logDirectory;

    public DiagnosticArchiveExporter(string logDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);
        _logDirectory = Path.GetFullPath(logDirectory);
    }

    public async Task<DiagnosticExportResult> ExportAsync(
        string destinationPath,
        DiagnosticSystemInfo systemInfo,
        DiagnosticCapabilities capabilities,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(destinationPath) || !string.Equals(Path.GetExtension(destinationPath), ".zip", StringComparison.OrdinalIgnoreCase))
            return DiagnosticExportResult.Fail(DiagnosticFailure.InvalidDestination);
        ArgumentNullException.ThrowIfNull(systemInfo);
        ArgumentNullException.ThrowIfNull(capabilities);

        string destination;
        try { destination = Path.GetFullPath(destinationPath); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        { return DiagnosticExportResult.Fail(DiagnosticFailure.InvalidDestination); }

        var parent = Path.GetDirectoryName(destination);
        if (string.IsNullOrWhiteSpace(parent)) return DiagnosticExportResult.Fail(DiagnosticFailure.InvalidDestination);
        var temporary = destination + $".{Guid.NewGuid():N}.tmp";
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(parent);
            await using (var output = new FileStream(temporary, OwnerOnlyPermissions.CreateFileOptions(
                FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None)))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                await WriteJsonAsync(archive, "system.json", DiagnosticSystemInfo.Create(
                    systemInfo.AppVersion, systemInfo.OperatingSystem, systemInfo.Distribution, systemInfo.Kernel,
                    systemInfo.Architecture, systemInfo.Desktop, systemInfo.SessionType),
                    DiagnosticsJson.Context.DiagnosticSystemInfo, cancellationToken).ConfigureAwait(false);
                await WriteJsonAsync(archive, "capabilities.json", capabilities,
                    DiagnosticsJson.Context.DiagnosticCapabilities, cancellationToken).ConfigureAwait(false);
                await WriteSanitizedLogsAsync(archive, cancellationToken).ConfigureAwait(false);
            }
            OwnerOnlyPermissions.ApplyFile(temporary);
            if (new FileInfo(temporary).Length > MaximumArchiveBytes)
            {
                File.Delete(temporary);
                return DiagnosticExportResult.Fail(DiagnosticFailure.ArchiveTooLarge);
            }
            File.Move(temporary, destination, overwrite: true);
            OwnerOnlyPermissions.ApplyFile(destination);
            return new DiagnosticExportResult(true, DiagnosticFailure.None, destination);
        }
        catch (OperationCanceledException)
        {
            TryDelete(temporary);
            return DiagnosticExportResult.Fail(DiagnosticFailure.Cancelled);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            TryDelete(temporary);
            return DiagnosticExportResult.Fail(DiagnosticFailure.DestinationUnavailable);
        }
    }

    private static async Task WriteJsonAsync<T>(ZipArchive archive, string name, T value,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        entry.LastWriteTime = StableZipTimestamp;
        await using var stream = entry.Open();
        await JsonSerializer.SerializeAsync(stream, value, typeInfo, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteSanitizedLogsAsync(ZipArchive archive, CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry("logs/events.jsonl", CompressionLevel.Optimal);
        entry.LastWriteTime = StableZipTimestamp;
        await using var output = new StreamWriter(entry.Open(), new UTF8Encoding(false), 4096, leaveOpen: false);
        var remaining = MaximumLogInputBytes;
        foreach (var path in EnumerateLogPaths())
        {
            if (remaining <= 0) break;
            FileInfo info;
            try { info = new FileInfo(path); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { continue; }
            if (!info.Exists || info.Length > remaining) continue;
            remaining -= (int)info.Length;
            using var input = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite), Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true, 4096, leaveOpen: false);
            while (await input.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (line.Length > 512) continue;
                try
                {
                    var item = JsonSerializer.Deserialize(line, DiagnosticsJson.Context.DiagnosticEvent);
                    if (item is null) continue;
                    await output.WriteLineAsync(JsonSerializer.Serialize(item, DiagnosticsJson.Context.DiagnosticEvent).AsMemory(), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (JsonException) { }
            }
        }
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private IEnumerable<string> EnumerateLogPaths()
    {
        for (var index = 9; index >= 1; index--) yield return Path.Combine(_logDirectory, $"diagnostics.log.{index}");
        yield return Path.Combine(_logDirectory, "diagnostics.log");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
