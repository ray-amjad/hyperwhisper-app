using HyperWhisper.Data.Entities;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.PortableApplication.Persistence;
using HyperWhisper.SpeechOutput;

namespace HyperWhisper.PortableApplication.Transcription;

/// <summary>
/// Portable recording/transcription state machine. A processing history row is
/// created before transcription and finalized as completed or failed.
/// </summary>
public sealed class TranscriptionWorkflow : IDisposable
{
    private readonly object _sync = new();
    private readonly IAudioRecorder _recorder;
    private readonly IAudioInputDeviceService _devices;
    private readonly IRecordedAudioTranscriber _transcriber;
    private readonly ITranscriptionPostProcessor? _postProcessor;
    private readonly ITextInjectionService? _textInjection;
    private readonly ITranscriptionHistoryStore _history;
    private readonly CompletedAudioRetention? _audioRetention;
    private readonly IBatchAudioPreprocessor? _audioPreprocessor;
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
        ITranscriptionHistoryStore history,
        ITranscriptionPostProcessor? postProcessor = null,
        ITextInjectionService? textInjection = null,
        bool ownsDependencies = false,
        CompletedAudioRetention? audioRetention = null,
        IBatchAudioPreprocessor? audioPreprocessor = null)
    {
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _transcriber = transcriber ?? throw new ArgumentNullException(nameof(transcriber));
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _audioRetention = audioRetention;
        _audioPreprocessor = audioPreprocessor;
        _postProcessor = postProcessor;
        _textInjection = textInjection;
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
            else if (IsActiveState(_state))
                result = BusyLocked();
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

        return await TranscribeAndPersistAsync(
            audioPath, duration, request, operation, ownsAudio: true,
            deleteOwnedAudioOnTerminalFailure: false, injectText: true, cancellationToken).ConfigureAwait(false);
    }

    public Task<PortableTranscriptionResult> TranscribeFileAsync(
        string audioPath,
        TranscriptionWorkflowRequest request,
        CancellationToken cancellationToken = default) =>
        TranscribeFileCoreAsync(audioPath, request, ownsAudio: false, cancellationToken);

    internal Task<PortableTranscriptionResult> TranscribeOwnedFileAsync(
        string audioPath,
        TranscriptionWorkflowRequest request,
        CancellationToken cancellationToken = default) =>
        TranscribeFileCoreAsync(audioPath, request, ownsAudio: true, cancellationToken);

    private async Task<PortableTranscriptionResult> TranscribeFileCoreAsync(
        string audioPath,
        TranscriptionWorkflowRequest request,
        bool ownsAudio,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath))
            return FailAndNotify("workflow.file_missing", "Choose an existing audio file.", PortableTranscriptionErrorCode.InvalidRequest);
        var fullAudioPath = Path.GetFullPath(audioPath);

        CancellationTokenSource operation;
        PortableTranscriptionResult? immediateFailure = null;
        lock (_sync)
        {
            var capability = _transcriber.Capability;
            if (!capability.IsAvailable)
                immediateFailure = FailLocked("workflow.backend_unavailable", capability.UnavailableReason ?? "No transcription backend is available.", PortableTranscriptionErrorCode.BackendUnavailable);
            else if (IsActiveState(_state))
                immediateFailure = BusyLocked();
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
        if (immediateFailure is not null)
        {
            if (ownsAudio) DeleteRecording(fullAudioPath);
            return immediateFailure;
        }
        return await TranscribeAndPersistAsync(
            fullAudioPath, TimeSpan.Zero, request, operation,
            ownsAudio, deleteOwnedAudioOnTerminalFailure: ownsAudio,
            injectText: false, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PortableTranscriptionResult> RetryTranscriptAsync(
        Guid transcriptId,
        TranscriptionWorkflowRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var frozenRequest = request.Snapshot();
        var retryStore = _history as ITranscriptionRetryStore;

        CancellationTokenSource operation;
        PortableTranscriptionResult? immediateFailure = null;
        lock (_sync)
        {
            var capability = _transcriber.Capability;
            if (!capability.IsAvailable)
                immediateFailure = FailLocked("workflow.backend_unavailable", capability.UnavailableReason ?? "No transcription backend is available.", PortableTranscriptionErrorCode.BackendUnavailable);
            else if (IsActiveState(_state))
                immediateFailure = BusyLocked();
            else if (frozenRequest.SelectedMode is null)
                immediateFailure = FailLocked("workflow.retry_mode_required", "Choose a mode to retry this transcription.", PortableTranscriptionErrorCode.InvalidRequest);
            else if (retryStore is null)
                immediateFailure = FailLocked("workflow.retry_unavailable", "This history store does not support retry.", PortableTranscriptionErrorCode.InvalidRequest);

            if (immediateFailure is not null) operation = null!;
            else
            {
                operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _activeOperation = operation;
                _cancelRequested = false;
                SetStateLocked(TranscriptionWorkflowState.Retrying, "Retrying transcription…", null);
            }
        }
        RaiseChanged();
        if (immediateFailure is not null) return immediateFailure;

        Transcript? claimedTranscript = null;
        try
        {
            var candidate = await _history.GetAsync(transcriptId, operation.Token).ConfigureAwait(false);
            if (candidate is null)
                return CompleteFailure("workflow.retry_not_found", "The transcript no longer exists.", PortableTranscriptionErrorCode.InvalidRequest, operation);
            if (candidate.Status != TranscriptStatus.Failed)
                return CompleteFailure("workflow.retry_not_failed", "Only failed transcriptions can be retried.", PortableTranscriptionErrorCode.InvalidRequest, operation);
            if (!TryResolveRetryAudio(candidate.AudioFilePath, out var audioPath))
                return CompleteFailure("workflow.retry_audio_unavailable", "The retry audio is missing or unsafe to open.", PortableTranscriptionErrorCode.InvalidRequest, operation);

            var started = await retryStore!.TryBeginRetryAsync(
                transcriptId, DateTime.UtcNow, operation.Token).ConfigureAwait(false);
            if (!started.IsStarted)
            {
                var (code, message) = started.Status switch
                {
                    HistoryRetryStartStatus.NotFound => ("workflow.retry_not_found", "The transcript no longer exists."),
                    HistoryRetryStartStatus.NotFailed => ("workflow.retry_not_failed", "Only failed transcriptions can be retried."),
                    _ => ("workflow.retry_conflict", "The transcript changed before retry could start."),
                };
                return CompleteFailure(code, message, PortableTranscriptionErrorCode.InvalidRequest, operation);
            }
            claimedTranscript = started.Transcript;

            // Recheck after the database claim so a swapped/deleted path never
            // reaches a transcription backend as a valid retry input.
            if (!TryResolveRetryAudio(started.Transcript!.AudioFilePath, out var claimedAudioPath)
                || !string.Equals(audioPath, claimedAudioPath, StringComparison.Ordinal))
            {
                return await CompleteTerminalFailureAsync(
                    started.Transcript,
                    "workflow.retry_audio_unavailable",
                    "The retry audio became unavailable.",
                    PortableTranscriptionErrorCode.InvalidRequest,
                    operation).ConfigureAwait(false);
            }

            return await TranscribeAndPersistAsync(
                claimedAudioPath, TimeSpan.FromSeconds(started.Transcript.Duration), frozenRequest, operation,
                ownsAudio: false, deleteOwnedAudioOnTerminalFailure: false,
                injectText: false, cancellationToken, started.Transcript,
                RetryCancellationSnapshot.Capture(started.Transcript)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested || cancellationToken.IsCancellationRequested)
        {
            return claimedTranscript is null
                ? CompleteCancelled(operation)
                : await CompleteRetryCancelledAsync(
                    claimedTranscript, RetryCancellationSnapshot.Capture(claimedTranscript), operation).ConfigureAwait(false);
        }
        catch (Exception)
        {
            if (claimedTranscript is not null)
                return await CompleteTerminalFailureAsync(
                    claimedTranscript,
                    "workflow.retry_failed",
                    "The transcription retry failed unexpectedly.",
                    PortableTranscriptionErrorCode.TranscriptionFailed,
                    operation).ConfigureAwait(false);
            return CompleteFailure(
                "workflow.retry_failed",
                "The transcription retry failed unexpectedly.",
                PortableTranscriptionErrorCode.TranscriptionFailed,
                operation);
        }
    }

    public Task CancelAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var shouldStopRecorder = false;
        lock (_sync)
        {
            if (!IsActiveState(_state))
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
            _textInjection?.Dispose();
        }
    }

    private async Task<PortableTranscriptionResult> TranscribeAndPersistAsync(
        string audioPath,
        TimeSpan duration,
        TranscriptionWorkflowRequest request,
        CancellationTokenSource operation,
        bool ownsAudio,
        bool deleteOwnedAudioOnTerminalFailure,
        bool injectText,
        CancellationToken callerToken,
        Transcript? existingTranscript = null,
        RetryCancellationSnapshot? retryCancellation = null)
    {
        var transcript = existingTranscript ?? new Transcript
        {
            Status = TranscriptStatus.Processing,
            Duration = duration.TotalSeconds,
            Date = DateTime.UtcNow,
            AudioFilePath = audioPath,
            TranscriptionProvider = _transcriber.Capability.DisplayName,
            Mode = request.SelectedMode?.Name ?? request.ModeName,
            ModeId = request.SelectedMode?.Id ?? request.ModeId,
        };
        try
        {
            if (existingTranscript is null)
                await _history.AddAsync(transcript, operation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested || callerToken.IsCancellationRequested)
        {
            return retryCancellation is not null
                ? await CompleteRetryCancelledAsync(transcript, retryCancellation, operation).ConfigureAwait(false)
                : CompleteCancelled(operation, audioPath, ownsAudio);
        }
        catch (Exception)
        {
            return CompleteFailure(
                "workflow.persistence_failed",
                "The processing transcription could not be saved.",
                PortableTranscriptionErrorCode.TranscriptionFailed,
                operation,
                audioPath,
                ownsAudio);
        }

        var transcriptionPath = audioPath;
        if (_audioPreprocessor is not null)
        {
            try
            {
                var preprocessed = await _audioPreprocessor.PreprocessAsync(audioPath, operation.Token).ConfigureAwait(false);
                if (Path.IsPathFullyQualified(preprocessed.TranscriptionPath)
                    && File.Exists(preprocessed.TranscriptionPath))
                {
                    transcriptionPath = preprocessed.TranscriptionPath;
                    transcript.TrimmedAudioFilePath = preprocessed.TrimmedAudioPath;
                    _ = await _history.UpdateAsync(transcript, operation.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (operation.IsCancellationRequested || callerToken.IsCancellationRequested)
            {
                return retryCancellation is not null
                    ? await CompleteRetryCancelledAsync(transcript, retryCancellation, operation).ConfigureAwait(false)
                    : await CompleteCancelledAsync(transcript, operation, audioPath, ownsAudio).ConfigureAwait(false);
            }
            catch { transcriptionPath = audioPath; transcript.TrimmedAudioFilePath = null; }
        }

        PortableTranscriptionResult result;
        try
        {
            result = await _transcriber.TranscribeAsync(transcriptionPath, request, operation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested || callerToken.IsCancellationRequested)
        {
            return retryCancellation is not null
                ? await CompleteRetryCancelledAsync(transcript, retryCancellation, operation).ConfigureAwait(false)
                : await CompleteCancelledAsync(transcript, operation, audioPath, ownsAudio).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return await CompleteTerminalFailureAsync(
                transcript,
                "workflow.backend_failed",
                "The transcription backend failed unexpectedly.",
                PortableTranscriptionErrorCode.TranscriptionFailed,
                operation,
                audioPath: audioPath,
                ownsAudio: deleteOwnedAudioOnTerminalFailure).ConfigureAwait(false);
        }

        if (operation.IsCancellationRequested || result.Failure?.Code == PortableTranscriptionErrorCode.Cancelled)
            return retryCancellation is not null
                ? await CompleteRetryCancelledAsync(transcript, retryCancellation, operation).ConfigureAwait(false)
                : await CompleteCancelledAsync(transcript, operation, audioPath, ownsAudio).ConfigureAwait(false);
        if (!result.IsSuccess)
            return await CompleteTerminalFailureAsync(
                transcript,
                $"workflow.{result.Failure?.Code.ToString().ToLowerInvariant() ?? "empty_result"}",
                result.Failure?.Message ?? "The transcription backend returned no text.",
                result.Failure?.Code ?? PortableTranscriptionErrorCode.TranscriptionFailed,
                operation,
                result.Provider,
                audioPath,
                deleteOwnedAudioOnTerminalFailure).ConfigureAwait(false);

        try
        {
            operation.Token.ThrowIfCancellationRequested();
            var rawText = result.Text!.Trim();
            string? postProcessedText = null;
            var (processingInput, postProcessingProvider) =
                await ApplyPostProcessingAsync(rawText, request, operation.Token).ConfigureAwait(false);

            var output = SpeechOutputProcessor.Process(new SpeechOutputProcessingRequest(
                processingInput,
                request.Language ?? request.SelectedMode?.Language ?? "auto",
                ToPortablePostProcessingMode(request.SelectedMode),
                request.VocabularyReplacements ?? [],
                request.ModeVocabularyReplacements ?? [],
                request.OutputOptions ?? BuildDefaultOutputOptions(request.SelectedMode),
                request.CursorContext));
            var finalText = output.TranscriptText;
            if (postProcessingProvider is not null) postProcessedText = finalText;

            if (operation.IsCancellationRequested || callerToken.IsCancellationRequested)
                return retryCancellation is not null
                    ? await CompleteRetryCancelledAsync(transcript, retryCancellation, operation).ConfigureAwait(false)
                    : await CompleteCancelledAsync(transcript, operation, audioPath, ownsAudio).ConfigureAwait(false);

            TextInjectionOutcome? injectionOutcome = null;
            if (injectText && _textInjection is not null)
            {
                try
                {
                    injectionOutcome = await TranscriptionTextDelivery.DeliverAsync(
                        _textInjection,
                        output.InjectionText,
                        request.PasteResultText,
                        operation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (operation.IsCancellationRequested)
                {
                    return retryCancellation is not null
                        ? await CompleteRetryCancelledAsync(transcript, retryCancellation, operation).ConfigureAwait(false)
                        : await CompleteCancelledAsync(
                            transcript, operation, audioPath, ownsAudio).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Paste/clipboard failure never invalidates a successful
                    // transcription; Linux injection already degrades to copy.
                    injectionOutcome = TextInjectionOutcome.Failed;
                }
            }

            PopulateCompletedTranscript(
                transcript, request, result, finalText, rawText, postProcessedText, postProcessingProvider);
            var deleteCompletedAudio = ownsAudio && _audioRetention is not null && !_audioRetention.ShouldKeepAudio;
            if (ownsAudio && _audioRetention is { ShouldKeepAudio: true })
            {
                var transformed = await _audioRetention.TransformAsync(audioPath, operation.Token).ConfigureAwait(false);
                if (transformed.IsSuccess) transcript.AudioFilePath = transformed.Value;
            }
            if (deleteCompletedAudio) transcript.AudioFilePath = null;
            if (!await _history.UpdateAsync(transcript, operation.Token).ConfigureAwait(false))
                throw new InvalidOperationException("The processing transcript disappeared before completion.");
            if (deleteCompletedAudio)
                await DeleteCompletedAudioAsync(audioPath, operation.Token).ConfigureAwait(false);
            lock (_sync)
            {
                FinishOperationLocked(operation);
                SetStateLocked(
                    TranscriptionWorkflowState.Completed,
                    BuildCompletionMessage(injectionOutcome),
                    null);
            }
            RaiseChanged();
            return PortableTranscriptionResult.Success(finalText, result.Provider ?? _transcriber.Capability.DisplayName) with
            {
                RawText = rawText,
                PostProcessedText = postProcessedText,
                PostProcessingProvider = postProcessingProvider,
                InjectionOutcome = injectionOutcome,
                Timestamps = request.StoreWordTimestamps ? result.Timestamps : null,
            };
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested || callerToken.IsCancellationRequested)
        {
            return retryCancellation is not null
                ? await CompleteRetryCancelledAsync(transcript, retryCancellation, operation).ConfigureAwait(false)
                : await CompleteCancelledAsync(transcript, operation, audioPath, ownsAudio).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return await CompleteTerminalFailureAsync(
                transcript,
                "workflow.persistence_failed",
                "The completed transcription could not be saved.",
                PortableTranscriptionErrorCode.TranscriptionFailed,
                operation,
                result.Provider,
                audioPath,
                deleteOwnedAudioOnTerminalFailure).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs the optional post-processor. Returns the text to feed into speech
    /// output and the provider to persist, or the raw text and no provider when
    /// post-processing is off, fails, is cancelled, or returns empty output.
    /// </summary>
    private async Task<(string ProcessingInput, string? Provider)> ApplyPostProcessingAsync(
        string rawText,
        TranscriptionWorkflowRequest request,
        CancellationToken cancellationToken)
    {
        if (!ShouldPostProcess(request.SelectedMode) || _postProcessor is null)
            return (rawText, null);

        PortablePostProcessingResult postProcessing;
        try
        {
            postProcessing = await _postProcessor.ProcessAsync(
                rawText,
                request.SelectedMode!,
                request.ApplicationContext,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            postProcessing = PortablePostProcessingResult.Skipped(
                rawText, "postprocessing.cancelled", "Post-processing was cancelled.");
        }
        catch (Exception)
        {
            postProcessing = PortablePostProcessingResult.Skipped(
                rawText, "postprocessing.failed", "Post-processing failed.");
        }

        // Windows keeps the raw transcription whenever post-processing
        // fails, is cancelled, or returns empty output. Never persist a
        // provider or synthetic completion unless enhancement applied.
        if (postProcessing.WasApplied
            && !string.IsNullOrWhiteSpace(postProcessing.Text)
            && !string.IsNullOrWhiteSpace(postProcessing.Provider))
            return (postProcessing.Text.Trim(), postProcessing.Provider);

        return (rawText, null);
    }

    private void PopulateCompletedTranscript(
        Transcript transcript,
        TranscriptionWorkflowRequest request,
        PortableTranscriptionResult result,
        string finalText,
        string rawText,
        string? postProcessedText,
        string? postProcessingProvider)
    {
        transcript.Text = finalText;
        transcript.TranscribedText = rawText;
        transcript.PostProcessedText = postProcessedText;
        transcript.WordTimestampsJson = request.StoreWordTimestamps
            ? result.Timestamps?.ToPersistedJson()
            : null;
        transcript.Status = TranscriptStatus.Completed;
        transcript.FailedReason = null;
        transcript.TranscriptionProvider = result.Provider ?? _transcriber.Capability.DisplayName;
        transcript.PostProcessingProvider = postProcessingProvider;
        transcript.Mode = request.SelectedMode?.Name ?? request.ModeName;
        transcript.ModeId = request.SelectedMode?.Id ?? request.ModeId;
    }

    private async Task DeleteCompletedAudioAsync(string audioPath, CancellationToken cancellationToken)
    {
        try
        {
            _ = await _audioRetention!.DeleteAsync(audioPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The transcript is complete and no longer references the file.
            // The recording-root inventory still reports the orphan.
        }
    }

    private static bool ShouldPostProcess(Mode? mode) =>
        mode is not null
        && (mode.PostProcessingMode == 1
            || (mode.PostProcessingMode == 2
                && string.Equals(mode.PostProcessingProvider, "local_llm", StringComparison.OrdinalIgnoreCase)));

    private static PortablePostProcessingMode ToPortablePostProcessingMode(Mode? mode) => mode?.PostProcessingMode switch
    {
        1 => PortablePostProcessingMode.Cloud,
        2 => PortablePostProcessingMode.Local,
        _ => PortablePostProcessingMode.Off,
    };

    private static SpeechOutputProcessingOptions BuildDefaultOutputOptions(Mode? mode) => new(
        RemoveFillerWords: true,
        RemoveTrailingPeriod: mode?.RemoveTrailingPeriod == true,
        AppendTrailingSpace: true,
        AutocapitalizeInsert: false,
        Punctuation: mode?.Punctuation ?? true,
        Capitalization: mode?.Capitalization ?? true,
        ProfanityFilter: mode?.ProfanityFilter ?? false);

    private async Task<PortableTranscriptionResult> CompleteTerminalFailureAsync(
        Transcript transcript,
        string code,
        string message,
        PortableTranscriptionErrorCode resultCode,
        CancellationTokenSource operation,
        string? provider = null,
        string? audioPath = null,
        bool ownsAudio = false)
    {
        if (ownsAudio && audioPath is not null)
        {
            transcript.AudioFilePath = null;
            DeleteRecording(audioPath);
        }
        transcript.Status = TranscriptStatus.Failed;
        transcript.FailedReason = message;
        transcript.Text = $"Transcription failed: {message}";
        if (!string.IsNullOrWhiteSpace(provider)) transcript.TranscriptionProvider = provider;
        try { await _history.UpdateAsync(transcript, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception) { }

        PortableTranscriptionResult result;
        lock (_sync)
        {
            FinishOperationLocked(operation);
            result = FailLocked(code, message, resultCode);
        }
        RaiseChanged();
        return result;
    }

    private async Task<PortableTranscriptionResult> CompleteCancelledAsync(
        Transcript transcript,
        CancellationTokenSource operation,
        string audioPath,
        bool ownsAudio)
    {
        var safeToDeleteOwnedAudio = false;
        try
        {
            safeToDeleteOwnedAudio = await _history.DeleteAsync(
                transcript.Id, CancellationToken.None).ConfigureAwait(false);
            if (!safeToDeleteOwnedAudio)
            {
                // A false result normally means the row is already gone. Check
                // explicitly so a custom/racing store can never leave a row
                // pointing at audio that cancellation then deletes.
                safeToDeleteOwnedAudio = await _history.GetAsync(
                    transcript.Id, CancellationToken.None).ConfigureAwait(false) is null;
            }
        }
        catch (Exception) { }

        if (!safeToDeleteOwnedAudio)
            await MarkCancellationCleanupFailedAsync(transcript).ConfigureAwait(false);

        return CompleteCancelled(
            operation,
            audioPath,
            ownsAudio && safeToDeleteOwnedAudio);
    }

    private async Task<PortableTranscriptionResult> CompleteRetryCancelledAsync(
        Transcript transcript,
        RetryCancellationSnapshot snapshot,
        CancellationTokenSource operation)
    {
        snapshot.Restore(transcript);
        transcript.Status = TranscriptStatus.Failed;
        try { _ = await _history.UpdateAsync(transcript, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception) { }
        return CompleteCancelled(operation);
    }

    private async Task MarkCancellationCleanupFailedAsync(Transcript transcript)
    {
        transcript.Status = TranscriptStatus.Failed;
        transcript.FailedReason = "Cancellation cleanup did not finish";
        transcript.Text = transcript.FailedReason;
        try { await _history.UpdateAsync(transcript, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception) { }
    }

    private static string BuildCompletionMessage(TextInjectionOutcome? outcome) => outcome switch
    {
        TextInjectionOutcome.Pasted => "Transcription pasted and saved to history",
        TextInjectionOutcome.CopiedToClipboard => "Transcription copied to clipboard and saved to history",
        TextInjectionOutcome.SecureFieldSkipped => "Transcription saved; secure field was not modified",
        TextInjectionOutcome.Failed => "Transcription saved, but text injection failed",
        _ => "Transcription saved to history",
    };

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

    private PortableTranscriptionResult BusyLocked() => PortableTranscriptionResult.Failed(
        PortableTranscriptionErrorCode.InvalidRequest,
        "A recording or transcription is already active.",
        _transcriber.Capability.DisplayName);

    private void SetStateLocked(TranscriptionWorkflowState state, string message, string? errorCode)
    {
        _state = state;
        _message = message;
        _errorCode = errorCode;
    }

    private static bool IsActiveState(TranscriptionWorkflowState state) => state is
        TranscriptionWorkflowState.Recording or TranscriptionWorkflowState.Stopping
        or TranscriptionWorkflowState.Transcribing or TranscriptionWorkflowState.Retrying;

    private static bool TryResolveRetryAudio(string? path, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            fullPath = Path.GetFullPath(path);
            var info = new FileInfo(fullPath);
            return info.Exists && !info.Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException
            or IOException or UnauthorizedAccessException)
        {
            fullPath = string.Empty;
            return false;
        }
    }

    private sealed record RetryCancellationSnapshot(
        string Text,
        string? TranscribedText,
        string? PostProcessedText,
        string? WordTimestampsJson,
        string? FailedReason,
        string? TranscriptionProvider,
        string? PostProcessingProvider,
        string? Mode,
        Guid? ModeId)
    {
        public static RetryCancellationSnapshot Capture(Transcript transcript) => new(
            transcript.Text,
            transcript.TranscribedText,
            transcript.PostProcessedText,
            transcript.WordTimestampsJson,
            transcript.FailedReason,
            transcript.TranscriptionProvider,
            transcript.PostProcessingProvider,
            transcript.Mode,
            transcript.ModeId);

        public void Restore(Transcript transcript)
        {
            transcript.Text = Text;
            transcript.TranscribedText = TranscribedText;
            transcript.PostProcessedText = PostProcessedText;
            transcript.WordTimestampsJson = WordTimestampsJson;
            transcript.FailedReason = FailedReason;
            transcript.TranscriptionProvider = TranscriptionProvider;
            transcript.PostProcessingProvider = PostProcessingProvider;
            transcript.Mode = Mode;
            transcript.ModeId = ModeId;
        }
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
