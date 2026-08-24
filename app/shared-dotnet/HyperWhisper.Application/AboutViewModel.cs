using HyperWhisper.Diagnostics;

namespace HyperWhisper.PortableApplication.ViewModels;

public sealed class AboutViewModel
{
    private readonly DiagnosticArchiveExporter _exporter;
    private readonly DiagnosticSystemInfo _systemInfo;
    private readonly DiagnosticCapabilities _capabilities;

    public AboutViewModel(
        string appVersion,
        string packageVersion,
        DiagnosticArchiveExporter exporter,
        DiagnosticSystemInfo systemInfo,
        DiagnosticCapabilities capabilities)
    {
        AppVersion = string.IsNullOrWhiteSpace(appVersion) ? "unknown" : appVersion;
        PackageVersion = string.IsNullOrWhiteSpace(packageVersion) ? "unknown" : packageVersion;
        _exporter = exporter ?? throw new ArgumentNullException(nameof(exporter));
        _systemInfo = systemInfo ?? throw new ArgumentNullException(nameof(systemInfo));
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
    }

    public string AppVersion { get; }
    public string PackageVersion { get; }
    public string UpdateDescription => "Linux updates are managed by APT. Use your package manager to check for and install releases.";
    public string DiagnosticsPrivacyDescription =>
        "The archive contains only filtered system metadata, capability booleans, and fixed-category event outcomes. It never contains settings, transcript or audio content, prompts, clipboard text, credentials, account identifiers, or paths.";
    public UiStatus Status { get; } = new();

    public async Task ExportDiagnosticsAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        Status.Busy("Exporting privacy-safe diagnostics…");
        var result = await _exporter.ExportAsync(destinationPath, _systemInfo, _capabilities, cancellationToken);
        if (result.Success) Status.Success("Privacy-safe diagnostics exported");
        else Status.Failure($"diagnostics.{result.Failure.ToString().ToLowerInvariant()}", "The diagnostics archive could not be exported.");
    }
}
