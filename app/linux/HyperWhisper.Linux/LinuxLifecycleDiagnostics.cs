using HyperWhisper.Diagnostics;

namespace HyperWhisper.Linux;

internal sealed class LinuxLifecycleDiagnostics
{
    private readonly Func<bool> _enabled;
    private readonly Func<DiagnosticEvent, CancellationToken, Task<DiagnosticWriteResult>> _write;

    internal LinuxLifecycleDiagnostics(PrivacySafeRotatingLogger logger, Func<bool> enabled)
        : this(enabled, logger.WriteAsync) { }

    internal LinuxLifecycleDiagnostics(
        Func<bool> enabled,
        Func<DiagnosticEvent, CancellationToken, Task<DiagnosticWriteResult>> write)
    {
        _enabled = enabled ?? throw new ArgumentNullException(nameof(enabled));
        _write = write ?? throw new ArgumentNullException(nameof(write));
    }

    internal async Task ReportAsync(DiagnosticComponent component, DiagnosticOutcome outcome)
    {
        if (!_enabled()) return;
        var severity = outcome switch
        {
            DiagnosticOutcome.Failed => DiagnosticSeverity.Error,
            DiagnosticOutcome.Degraded or DiagnosticOutcome.Unavailable => DiagnosticSeverity.Warning,
            _ => DiagnosticSeverity.Information,
        };
        try
        {
            _ = await _write(new DiagnosticEvent(DateTimeOffset.UtcNow, severity, component, outcome),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Diagnostics are best-effort and must never affect transcription.
        }
    }
}
