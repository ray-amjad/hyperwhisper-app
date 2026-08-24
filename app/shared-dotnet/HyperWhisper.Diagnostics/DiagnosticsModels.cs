using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace HyperWhisper.Diagnostics;

public enum DiagnosticSeverity { Information, Warning, Error }
public enum DiagnosticComponent { Application, Audio, Clipboard, GlobalShortcut, Inference, Portal, SpeechOutput, Storage, Transcription }
public enum DiagnosticOutcome { Started, Succeeded, Failed, Cancelled, Unavailable, Degraded }
public enum DiagnosticFailure { None, Cancelled, InvalidDestination, DestinationUnavailable, LogUnavailable, ArchiveTooLarge }

public sealed record DiagnosticEvent(
    DateTimeOffset TimestampUtc,
    DiagnosticSeverity Severity,
    DiagnosticComponent Component,
    DiagnosticOutcome Outcome);

public sealed record DiagnosticCapabilities(
    bool AudioCapture,
    bool Clipboard,
    bool GlobalShortcuts,
    bool TextInjection,
    bool PortalScreenCapture,
    bool LocalInference,
    bool Cuda);

public sealed record DiagnosticSystemInfo(
    string AppVersion,
    string OperatingSystem,
    string Distribution,
    string Kernel,
    string Architecture,
    string Desktop,
    string SessionType)
{
    public static DiagnosticSystemInfo Detect(string appVersion)
    {
        var os = System.OperatingSystem.IsLinux() ? "Linux"
            : System.OperatingSystem.IsWindows() ? "Windows"
            : System.OperatingSystem.IsMacOS() ? "macOS"
            : "Other";
        return Create(
            appVersion,
            os,
            ReadDistribution(),
            RuntimeInformation.OSDescription,
            RuntimeInformation.OSArchitecture.ToString(),
            Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "unknown",
            Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "unknown");
    }

    public static DiagnosticSystemInfo Create(
        string? appVersion,
        string? operatingSystem,
        string? distribution,
        string? kernel,
        string? architecture,
        string? desktop,
        string? sessionType) => new(
            SafeValue(appVersion), SafeValue(operatingSystem), SafeValue(distribution),
            SafeValue(kernel), SafeValue(architecture), SafeValue(desktop), SafeValue(sessionType));

    private static string ReadDistribution()
    {
        try
        {
            foreach (var line in File.ReadLines("/etc/os-release").Take(64))
            {
                if (!line.StartsWith("PRETTY_NAME=", StringComparison.Ordinal)) continue;
                return line[12..].Trim().Trim('"');
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return "unknown";
    }

    internal static string SafeValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "unknown";
        var trimmed = value.Trim();
        if (trimmed.Length > 96 || trimmed.Contains('/') || trimmed.Contains('\\') ||
            trimmed.Contains('@') || trimmed.Contains('=') || trimmed.Contains(':') ||
            !Regex.IsMatch(trimmed, "^[a-zA-Z0-9 ._()+-]+$", RegexOptions.CultureInvariant))
            return "redacted";
        return trimmed;
    }
}

public sealed record DiagnosticWriteResult(bool Success, DiagnosticFailure Failure)
{
    public static DiagnosticWriteResult Ok { get; } = new(true, DiagnosticFailure.None);
    public static DiagnosticWriteResult Fail(DiagnosticFailure failure) => new(false, failure);
}

public sealed record DiagnosticExportResult(bool Success, DiagnosticFailure Failure, string? ArchivePath)
{
    public static DiagnosticExportResult Fail(DiagnosticFailure failure) => new(false, failure, null);
}
