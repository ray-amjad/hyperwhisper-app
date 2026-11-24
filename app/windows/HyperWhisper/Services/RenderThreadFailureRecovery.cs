using System.Runtime.InteropServices;

namespace HyperWhisper.Services;

/// <summary>
/// Recovery for a failed WPF render thread (#2549): <c>COMException
/// UCEERR_RENDERTHREADFAILURE (0x88980406)</c>, usually a lost or reset GPU device
/// after a driver update, sleep/resume or a display change.
///
/// WPF raises it again on every later render and resize, so the generic
/// "unhandled error" dialog turned one fault into a dialog per repaint. This
/// handles it instead: it switches the process to software rendering once (the
/// documented WPF fallback for a lost device), reports it to Sentry once with a
/// fixed fingerprint, and never shows a dialog.
/// </summary>
/// <remarks>
/// An instance holds the once-per-process state so a test can drive a fresh one;
/// the app uses <see cref="Shared"/>.
/// </remarks>
internal sealed class RenderThreadFailureRecovery
{
    internal const int RenderThreadFailureHResult = unchecked((int)0x88980406);

    internal const string Message = "WPF render thread failed";

    internal static readonly string[] Fingerprint = ["wpf-render-thread-failure"];

    private const int MaxChainLength = 32;

    internal static RenderThreadFailureRecovery Shared { get; } = new();

    private readonly object _gate = new();
    private bool _handledOnce;
    private int _repeatCount;

    internal sealed record Report(
        Exception Exception,
        string Message,
        string[] Fingerprint,
        Dictionary<string, string> Tags,
        Dictionary<string, object> Extras);

    /// <summary>
    /// True when <paramref name="exception"/>, or any exception in its chain, is a
    /// render thread failure.
    /// </summary>
    internal static bool IsRenderThreadFailure(Exception? exception)
    {
        var depth = 0;
        for (var current = exception; current != null && depth < MaxChainLength; current = current.InnerException, depth++)
        {
            if (current is COMException { HResult: RenderThreadFailureHResult })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Handles <paramref name="exception"/> when it is a render thread failure and
    /// returns true; returns false, and does nothing, for any other exception.
    ///
    /// The first failure runs <paramref name="switchToSoftwareRendering"/> and then
    /// <paramref name="send"/>, each once per instance. A repeat is only logged
    /// (the first one, then every 100th). Neither callback may escape: the caller
    /// is the dispatcher's unhandled-exception handler.
    /// </summary>
    internal bool TryHandle(
        Exception? exception,
        string stage,
        Action switchToSoftwareRendering,
        Action<Report> send)
    {
        if (!IsRenderThreadFailure(exception))
        {
            return false;
        }

        lock (_gate)
        {
            if (_handledOnce)
            {
                _repeatCount++;
                if (_repeatCount == 1 || _repeatCount % 100 == 0)
                {
                    LoggingService.Warn(
                        $"RenderThreadFailureRecovery: render thread failed again after recovery " +
                        $"(repeats={_repeatCount}, stage={stage})");
                }

                return true;
            }

            _handledOnce = true;
        }

        LoggingService.Error(
            $"RenderThreadFailureRecovery: render thread failed (stage={stage}), switching to software rendering",
            exception!);

        string recovery;
        try
        {
            switchToSoftwareRendering();
            recovery = "software_rendering";
        }
        catch (Exception fallbackException)
        {
            recovery = "software_rendering_failed";
            LoggingService.Error(
                $"RenderThreadFailureRecovery: software rendering switch failed " +
                $"(exception_type={fallbackException.GetType().Name})");
        }

        try
        {
            send(new Report(
                exception!,
                Message,
                Fingerprint,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["component"] = "wpf_rendering",
                    ["diagnostic_name"] = "wpf_render_thread_failure",
                    ["render_recovery"] = recovery,
                    ["capture_stage"] = stage,
                },
                new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["hresult"] = "0x88980406",
                    ["render_recovery"] = recovery,
                    ["capture_stage"] = stage,
                }));
        }
        catch (Exception reportException)
        {
            LoggingService.Error(
                $"RenderThreadFailureRecovery: report failed (exception_type={reportException.GetType().Name})");
        }

        return true;
    }

    /// <summary>Sends <paramref name="report"/> to Sentry, keeping the real stack.</summary>
    internal static void SendReport(Report report) =>
        SentryService.Capture(
            report.Exception,
            message: report.Message,
            extras: report.Extras,
            tags: report.Tags,
            fingerprint: report.Fingerprint);
}
