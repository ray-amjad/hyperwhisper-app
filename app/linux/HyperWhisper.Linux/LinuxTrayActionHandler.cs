using HyperWhisper.Linux.Platform.Desktop;
using HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.Linux;

internal sealed class LinuxTrayActionHandler : IDisposable
{
    private readonly Func<bool> _isRecording;
    private readonly Func<bool> _isImporting;
    private readonly Func<CancellationToken, Task> _start;
    private readonly Func<CancellationToken, Task> _stop;
    private readonly Func<CancellationToken, Task> _transcribeFile;
    private readonly IReadOnlyDictionary<StatusNotifierAction, Action> _immediateActions;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly Func<string, string> _text;
    private bool _disposed;

    internal LinuxTrayActionHandler(
        Func<bool> isRecording,
        Func<bool> isImporting,
        Func<CancellationToken, Task> start,
        Func<CancellationToken, Task> stop,
        Func<CancellationToken, Task> transcribeFile,
        Action selectDefaultMicrophone,
        Action selectPreviousMicrophone,
        Action selectNextMicrophone,
        Action cycleMode,
        Action openHistory,
        Action openSettings,
        Action openHelp,
        Action openSupport,
        Action sendFeedback,
        Action show,
        Action hide,
        Action quit,
        Func<string, string> text)
    {
        _isRecording = isRecording;
        _isImporting = isImporting;
        _start = start;
        _stop = stop;
        _transcribeFile = transcribeFile;
        _text = text ?? throw new ArgumentNullException(nameof(text));
        _immediateActions = new Dictionary<StatusNotifierAction, Action>
        {
            [StatusNotifierAction.SelectDefaultMicrophone] = selectDefaultMicrophone,
            [StatusNotifierAction.SelectPreviousMicrophone] = selectPreviousMicrophone,
            [StatusNotifierAction.SelectNextMicrophone] = selectNextMicrophone,
            [StatusNotifierAction.CycleMode] = cycleMode,
            [StatusNotifierAction.OpenHistory] = openHistory,
            [StatusNotifierAction.OpenSettings] = openSettings,
            [StatusNotifierAction.OpenHelp] = openHelp,
            [StatusNotifierAction.OpenSupport] = openSupport,
            [StatusNotifierAction.SendFeedback] = sendFeedback,
            [StatusNotifierAction.Show] = show,
            [StatusNotifierAction.Hide] = hide,
            [StatusNotifierAction.Quit] = quit,
        };
    }

    internal async Task<PlatformResult> HandleAsync(
        StatusNotifierAction action,
        CancellationToken cancellationToken = default)
    {
        if (_disposed)
            return PlatformResult.Failure("tray.disposed", _text("linux.error.tray_disposed"));
        if (action is StatusNotifierAction.CycleMode or StatusNotifierAction.OpenHistory
            or StatusNotifierAction.OpenSettings or StatusNotifierAction.OpenHelp
            or StatusNotifierAction.OpenSupport or StatusNotifierAction.SendFeedback
            or StatusNotifierAction.Show or StatusNotifierAction.Hide or StatusNotifierAction.Quit)
        {
            try
            {
                _immediateActions[action]();
                return PlatformResult.Success();
            }
            catch
            {
                return PlatformResult.Failure("tray.action_failed", _text("linux.error.tray_action_failed"));
            }
        }
        try
        {
            if (!await _operation.WaitAsync(0, cancellationToken).ConfigureAwait(true))
                return PlatformResult.Failure("tray.busy", _text("linux.error.tray_busy"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return PlatformResult.Failure("tray.cancelled", _text("linux.error.tray_cancelled"));
        }
        try
        {
            switch (action)
            {
                case StatusNotifierAction.StartRecording:
                    if (_isRecording() || _isImporting()) return UnsafeState();
                    await _start(cancellationToken).ConfigureAwait(true);
                    break;
                case StatusNotifierAction.StopRecording:
                    if (!_isRecording() || _isImporting()) return UnsafeState();
                    await _stop(cancellationToken).ConfigureAwait(true);
                    break;
                case StatusNotifierAction.TranscribeFile:
                    if (_isRecording() || _isImporting()) return UnsafeState();
                    await _transcribeFile(cancellationToken).ConfigureAwait(true);
                    break;
                case StatusNotifierAction.SelectDefaultMicrophone:
                case StatusNotifierAction.SelectPreviousMicrophone:
                case StatusNotifierAction.SelectNextMicrophone:
                    if (_isRecording() || _isImporting()) return UnsafeState();
                    _immediateActions[action]();
                    break;
                default:
                    return PlatformResult.Failure("tray.action_unknown", _text("linux.error.tray_action_unknown"));
            }
            return PlatformResult.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return PlatformResult.Failure("tray.cancelled", _text("linux.error.tray_cancelled"));
        }
        catch
        {
            return PlatformResult.Failure("tray.action_failed", _text("linux.error.tray_action_failed"));
        }
        finally
        {
            _operation.Release();
        }
    }

    private PlatformResult UnsafeState() => PlatformResult.Failure(
        "tray.unsafe_state", _text("linux.error.tray_unsafe_state"));

    public void Dispose()
    {
        _disposed = true;
    }
}
