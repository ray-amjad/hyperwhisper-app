using HyperWhisper.Data.Entities;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.PortableApplication.Persistence;

namespace HyperWhisper.PortableApplication.Transcription;

public enum TranscriptionWorkflowState
{
    Idle,
    Recording,
    Stopping,
    Transcribing,
    Completed,
    Cancelled,
    Failed,
}

public sealed record TranscriptionBackendCapability(
    bool IsAvailable,
    string DisplayName,
    string? UnavailableReason = null);

public enum PortableTranscriptionErrorCode
{
    InvalidRequest,
    BackendUnavailable,
    TranscriptionFailed,
    Cancelled,
}

public sealed record PortableTranscriptionFailure(
    PortableTranscriptionErrorCode Code,
    string Message);

public sealed record PortableTranscriptionResult(
    string? Text,
    string? Provider,
    PortableTranscriptionFailure? Failure)
{
    public bool IsSuccess => Failure is null && !string.IsNullOrWhiteSpace(Text);

    public static PortableTranscriptionResult Success(string text, string provider) =>
        new(text, provider, null);

    public static PortableTranscriptionResult Failed(
        PortableTranscriptionErrorCode code,
        string message,
        string? provider = null) =>
        new(null, provider, new PortableTranscriptionFailure(code, message));
}

public interface IRecordedAudioTranscriber
{
    TranscriptionBackendCapability Capability { get; }

    Task<PortableTranscriptionResult> TranscribeAsync(
        string audioPath,
        string? language,
        CancellationToken cancellationToken = default);
}

public sealed record TranscriptionWorkflowRequest(
    string? Language = null,
    string? ModeName = null,
    Guid? ModeId = null);

public sealed record TranscriptionWorkflowSnapshot(
    TranscriptionWorkflowState State,
    string Message,
    string? ErrorCode,
    IReadOnlyList<AudioInputDevice> AudioDevices,
    string? SelectedAudioDeviceId,
    TranscriptionBackendCapability Backend)
{
    public bool CanStartRecording => State is not (TranscriptionWorkflowState.Recording
        or TranscriptionWorkflowState.Stopping or TranscriptionWorkflowState.Transcribing)
        && Backend.IsAvailable && AudioDevices.Count > 0;

    public bool CanTranscribeFile => State is not (TranscriptionWorkflowState.Recording
        or TranscriptionWorkflowState.Stopping or TranscriptionWorkflowState.Transcribing)
        && Backend.IsAvailable;

    public bool CanStop => State == TranscriptionWorkflowState.Recording;
    public bool CanCancel => State is TranscriptionWorkflowState.Recording
        or TranscriptionWorkflowState.Stopping or TranscriptionWorkflowState.Transcribing;
}

/// <summary>
/// Portable recording/transcription state machine. History is written only
/// after a backend returns a non-empty successful transcription.
/// </summary>
public sealed class TranscriptionWorkflow : IDisposable
{
    private readonly object _sync = new();
    private readonly IAudioRecorder _recorder;
    private readonly IAudioInputDeviceService _devices;
    private readonly IRecordedAudioTranscriber _transcriber;
    private readonly HistoryRepository _history;
    private readonly bool _ownsDependencies;
    private IReadOnlyList<AudioInputDevice> _audioDevices = [];
    private string? _selectedDeviceId;
    private CancellationTokenSource? _activeOperation;
    private bool _cancelRequested;
    private bool _disposed;
    private TranscriptionWorkflowState _state = TranscriptionWorkflowState.Idle;
    private string _message = "Ready";
    private string? _errorCode;

    public TranscriptionWorkflow(
        IAudioRecorder recorder,
        IAudioInputDeviceService devices,
        IRecordedAudioTranscriber transcriber,
        HistoryRepository history,
        bool ownsDependencies = false)
    {
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _transcriber = transcriber ?? throw new ArgumentNullException(nameof(transcriber));
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _ownsDependencies = ownsDependencies;
        _devices.DevicesChanged += OnDevicesChanged;
    }

    public event EventHandler? Changed;

    public TranscriptionWorkflowSnapshot Snapshot
    {
        get
        {
            lock (_sync)
            {
                return new TranscriptionWorkflowSnapshot(
                    _state,
                    _message,
                    _errorCode,
                    _audioDevices,
                    _selectedDeviceId,
                    _transcriber.Capability);
            }
        }
    }

    public void RefreshDevices()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var result = _devices.GetAvailableDevices();
        lock (_sync)
        {
            if (result.IsFailure)
            {
                _audioDevices = [];
                _selectedDeviceId = null;
                SetStateLocked(_state, result.Error!.Message, result.Error.Code);
            }
            else
            {
                _audioDevices = result.Value ?? [];
                if (_selectedDeviceId is null || !_audioDevices.Any(item => item.Id == _selectedDeviceId))
                    _selectedDeviceId = _audioDevices.FirstOrDefault(item => item.IsDefault)?.Id
                        ?? _audioDevices.FirstOrDefault()?.Id;
                if (_state == TranscriptionWorkflowState.Idle)
                    SetStateLocked(_state, BuildAvailabilityMessage(), null);
            }
        }
        RaiseChanged();
    }

    public void SelectDevice(string? deviceId)
    {
        lock (_sync)
        {
            _selectedDeviceId = _audioDevices.Any(item => item.Id == deviceId) ? deviceId : null;
        }
        RaiseChanged();
    }

    public Task<PortableTranscriptionResult> StartRecordingAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (cancellationToken.IsCancellationRequested)
        {
            lock (_sync) SetStateLocked(TranscriptionWorkflowState.Cancelled, "Recording start cancelled", "workflow.cancelled");
            RaiseChanged();
            return Task.FromResult(PortableTranscriptionResult.Failed(
                PortableTranscriptionErrorCode.Cancelled,
                "Recording start was cancelled."));
        }
        PortableTranscriptionResult result;
        lock (_sync)
        {
            var capability = _transcriber.Capability;
            if (!capability.IsAvailable)
                result = FailLocked("workflow.backend_unavailable", capability.UnavailableReason ?? "No transcription backend is available.", PortableTranscriptionErrorCode.BackendUnavailable);
            else if (_selectedDeviceId is null)
                result = FailLocked("workflow.no_audio_device", "No audio input device is available.", PortableTranscriptionErrorCode.BackendUnavailable);
            else if (_state is TranscriptionWorkflowState.Recording or TranscriptionWorkflowState.Stopping or TranscriptionWorkflowState.Transcribing)
                result = FailLocked("workflow.busy", "A recording or transcription is already active.", PortableTranscriptionErrorCode.InvalidRequest);
            else
            {
                var started = _recorder.Start(new AudioRecordingOptions(_selectedDeviceId));
                if (started.IsFailure)
                    result = FailLocked(started.Error!.Code, started.Error.Message, PortableTranscriptionErrorCode.TranscriptionFailed);
                else
                {
                    _cancelRequested = false;
                    SetStateLocked(TranscriptionWorkflowState.Recording, "Recording…", null);
                    result = PortableTranscriptionResult.Success("Recording started", capability.DisplayName);
                }
            }
        }
        RaiseChanged();
        return Task.FromResult(result);
    }

    public async Task<PortableTranscriptionResult> StopAndTranscribeAsync(
        TranscriptionWorkflowRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);
        CancellationTokenSource operation;
        PortableTranscriptionResult? immediateFailure = null;
        lock (_sync)
        {
            if (_state != TranscriptionWorkflowState.Recording)
            {
                immediateFailure = FailLocked("workflow.not_recording", "No recording is active.", PortableTranscriptionErrorCode.InvalidRequest);
                operation = null!;
            }
            else
            {
                SetStateLocked(TranscriptionWorkflowState.Stopping, "Finishing recording…", null);
                operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _activeOperation = operation;
            }
        }
        RaiseChanged();
        if (immediateFailure is not null) return immediateFailure;

        var duration = _recorder.Duration;
        var stopped = _recorder.Stop();
        if (stopped.IsFailure)
            return CompleteFailure(stopped.Error!.Code, stopped.Error.Message, PortableTranscriptionErrorCode.TranscriptionFailed, operation);

        var audioPath = stopped.Value!;
        var cancelledAfterStop = false;
        lock (_sync)
        {
            if (_cancelRequested || operation.IsCancellationRequested)
            {
                DeleteRecording(audioPath);
                FinishOperationLocked(operation);
                SetStateLocked(TranscriptionWorkflowState.Cancelled, "Transcription cancelled", "workflow.cancelled");
                cancelledAfterStop = true;
            }
            else SetStateLocked(TranscriptionWorkflowState.Transcribing, "Transcribing recording…", null);
        }
        RaiseChanged();
        if (cancelledAfterStop)
            return PortableTranscriptionResult.Failed(PortableTranscriptionErrorCode.Cancelled, "Transcription was cancelled.");

        return await TranscribeAndPersistAsync(audioPath, duration, request, operation, ownsAudio: true, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PortableTranscriptionResult> TranscribeFileAsync(
        string audioPath,
        TranscriptionWorkflowRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath))
            return FailAndNotify("workflow.file_missing", "Choose an existing audio file.", PortableTranscriptionErrorCode.InvalidRequest);

        CancellationTokenSource operation;
        PortableTranscriptionResult? immediateFailure = null;
        lock (_sync)
        {
            var capability = _transcriber.Capability;
            if (!capability.IsAvailable)
                immediateFailure = FailLocked("workflow.backend_unavailable", capability.UnavailableReason ?? "No transcription backend is available.", PortableTranscriptionErrorCode.BackendUnavailable);
            else if (_state is TranscriptionWorkflowState.Recording or TranscriptionWorkflowState.Stopping or TranscriptionWorkflowState.Transcribing)
                immediateFailure = FailLocked("workflow.busy", "A recording or transcription is already active.", PortableTranscriptionErrorCode.InvalidRequest);
            if (immediateFailure is not null) operation = null!;
            else
            {
                operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _activeOperation = operation;
                _cancelRequested = false;
                SetStateLocked(TranscriptionWorkflowState.Transcribing, "Transcribing file…", null);
            }
        }
        RaiseChanged();
        if (immediateFailure is not null) return immediateFailure;
        return await TranscribeAndPersistAsync(Path.GetFullPath(audioPath), TimeSpan.Zero, request, operation, ownsAudio: false, cancellationToken).ConfigureAwait(false);
    }

    public Task CancelAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var shouldStopRecorder = false;
        lock (_sync)
        {
            if (_state is not (TranscriptionWorkflowState.Recording or TranscriptionWorkflowState.Stopping or TranscriptionWorkflowState.Transcribing))
                return Task.CompletedTask;
            shouldStopRecorder = _state == TranscriptionWorkflowState.Recording;
            _cancelRequested = true;
            _activeOperation?.Cancel();
            SetStateLocked(TranscriptionWorkflowState.Cancelled, "Cancelled", "workflow.cancelled");
        }
        string? cancelledPath = null;
        if (shouldStopRecorder && _recorder.IsRecording)
        {
            var stopped = _recorder.Stop();
            if (stopped.IsSuccess) cancelledPath = stopped.Value;
        }
        if (cancelledPath is not null) DeleteRecording(cancelledPath);
        RaiseChanged();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        var shouldStopRecorder = false;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _activeOperation?.Cancel();
            shouldStopRecorder = _state == TranscriptionWorkflowState.Recording && _recorder.IsRecording;
        }
        _devices.DevicesChanged -= OnDevicesChanged;
        if (shouldStopRecorder)
        {
            var stopped = _recorder.Stop();
            if (stopped.IsSuccess) DeleteRecording(stopped.Value!);
        }
        if (_ownsDependencies)
        {
            _recorder.Dispose();
            _devices.Dispose();
            if (_transcriber is IDisposable disposable) disposable.Dispose();
        }
    }

    private async Task<PortableTranscriptionResult> TranscribeAndPersistAsync(
        string audioPath,
        TimeSpan duration,
        TranscriptionWorkflowRequest request,
        CancellationTokenSource operation,
        bool ownsAudio,
        CancellationToken callerToken)
    {
        PortableTranscriptionResult result;
        try
        {
            result = await _transcriber.TranscribeAsync(audioPath, request.Language, operation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested || callerToken.IsCancellationRequested)
        {
            return CompleteCancelled(operation, audioPath, ownsAudio);
        }
        catch (Exception)
        {
            return CompleteFailure("workflow.backend_failed", "The transcription backend failed unexpectedly.", PortableTranscriptionErrorCode.TranscriptionFailed, operation, audioPath, ownsAudio);
        }

        if (operation.IsCancellationRequested || result.Failure?.Code == PortableTranscriptionErrorCode.Cancelled)
            return CompleteCancelled(operation, audioPath, ownsAudio);
        if (!result.IsSuccess)
            return CompleteFailure(
                $"workflow.{result.Failure?.Code.ToString().ToLowerInvariant() ?? "empty_result"}",
                result.Failure?.Message ?? "The transcription backend returned no text.",
                result.Failure?.Code ?? PortableTranscriptionErrorCode.TranscriptionFailed,
                operation,
                audioPath,
                ownsAudio);

        try
        {
            operation.Token.ThrowIfCancellationRequested();
            var transcript = new Transcript
            {
                Text = result.Text!.Trim(),
                TranscribedText = result.Text.Trim(),
                Status = TranscriptStatus.Completed,
                Duration = duration.TotalSeconds,
                Date = DateTime.UtcNow,
                AudioFilePath = audioPath,
                TranscriptionProvider = result.Provider ?? _transcriber.Capability.DisplayName,
                Mode = request.ModeName,
                ModeId = request.ModeId,
            };
            await _history.AddAsync(transcript, operation.Token).ConfigureAwait(false);
            lock (_sync)
            {
                FinishOperationLocked(operation);
                SetStateLocked(TranscriptionWorkflowState.Completed, "Transcription saved to history", null);
            }
            RaiseChanged();
            return result;
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested || callerToken.IsCancellationRequested)
        {
            return CompleteCancelled(operation, audioPath, ownsAudio);
        }
        catch (Exception)
        {
            return CompleteFailure("workflow.persistence_failed", "The completed transcription could not be saved.", PortableTranscriptionErrorCode.TranscriptionFailed, operation, audioPath, ownsAudio);
        }
    }

    private PortableTranscriptionResult CompleteFailure(
        string code,
        string message,
        PortableTranscriptionErrorCode resultCode,
        CancellationTokenSource operation,
        string? audioPath = null,
        bool ownsAudio = false)
    {
        if (ownsAudio && audioPath is not null) DeleteRecording(audioPath);
        PortableTranscriptionResult result;
        lock (_sync)
        {
            FinishOperationLocked(operation);
            result = FailLocked(code, message, resultCode);
        }
        RaiseChanged();
        return result;
    }

    private PortableTranscriptionResult CompleteCancelled(
        CancellationTokenSource operation,
        string? audioPath = null,
        bool ownsAudio = false)
    {
        if (ownsAudio && audioPath is not null) DeleteRecording(audioPath);
        lock (_sync)
        {
            FinishOperationLocked(operation);
            SetStateLocked(TranscriptionWorkflowState.Cancelled, "Transcription cancelled", "workflow.cancelled");
        }
        RaiseChanged();
        return PortableTranscriptionResult.Failed(PortableTranscriptionErrorCode.Cancelled, "Transcription was cancelled.");
    }

    private void FinishOperationLocked(CancellationTokenSource operation)
    {
        if (ReferenceEquals(_activeOperation, operation)) _activeOperation = null;
        operation.Dispose();
    }

    private PortableTranscriptionResult FailAndNotify(string code, string message, PortableTranscriptionErrorCode resultCode)
    {
        PortableTranscriptionResult result;
        lock (_sync) result = FailLocked(code, message, resultCode);
        RaiseChanged();
        return result;
    }

    private PortableTranscriptionResult FailLocked(string code, string message, PortableTranscriptionErrorCode resultCode)
    {
        SetStateLocked(TranscriptionWorkflowState.Failed, message, code);
        return PortableTranscriptionResult.Failed(resultCode, message, _transcriber.Capability.DisplayName);
    }

    private void SetStateLocked(TranscriptionWorkflowState state, string message, string? errorCode)
    {
        _state = state;
        _message = message;
        _errorCode = errorCode;
    }

    private string BuildAvailabilityMessage()
    {
        var capability = _transcriber.Capability;
        if (!capability.IsAvailable) return capability.UnavailableReason ?? "Transcription backend unavailable";
        return _audioDevices.Count == 0 ? "No audio input device found" : $"{capability.DisplayName} ready";
    }

    private void OnDevicesChanged(object? sender, EventArgs e) => RefreshDevices();

    private void RaiseChanged()
    {
        var handlers = Changed;
        if (handlers is null) return;
        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try { handler(this, EventArgs.Empty); }
            catch (Exception) { }
        }
    }

    private static void DeleteRecording(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
