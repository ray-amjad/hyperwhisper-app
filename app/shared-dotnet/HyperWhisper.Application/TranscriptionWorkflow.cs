using HyperWhisper.Data.Entities;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.PortableApplication.Persistence;
using HyperWhisper.SpeechOutput;
using HyperWhisper.SharedCore;

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
    PortableTranscriptionFailure? Failure,
    string? RawText = null,
    string? PostProcessedText = null,
    string? PostProcessingProvider = null,
    TextInjectionOutcome? InjectionOutcome = null)
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
        TranscriptionWorkflowRequest request,
        CancellationToken cancellationToken = default) =>
        TranscribeAsync(audioPath, request.Language, cancellationToken);

    // Compatibility entry point for fixed local backends. Mode-aware routers
    // override the request overload above; existing platform implementations
    // continue to receive the normalized language without losing compatibility.
    Task<PortableTranscriptionResult> TranscribeAsync(
        string audioPath,
        string? language,
        CancellationToken cancellationToken = default);
}

public sealed record PortablePostProcessingResult(
    string Text,
    bool WasApplied,
    string? Provider,
    string? FailureCode = null,
    string? FailureMessage = null)
{
    public static PortablePostProcessingResult Applied(string text, string provider) =>
        new(text, true, provider);

    public static PortablePostProcessingResult Skipped(
        string original,
        string? failureCode = null,
        string? failureMessage = null) =>
        new(original, false, null, failureCode, failureMessage);
}

public interface ITranscriptionPostProcessor
{
    Task<PortablePostProcessingResult> ProcessAsync(
        string transcript,
        Mode mode,
        ApplicationContextSnapshot? applicationContext,
        CancellationToken cancellationToken = default) =>
        ProcessAsync(transcript, mode, cancellationToken);

    // Compatibility entry point for processors that do not consume desktop
    // context. New processors should override the context-aware overload.
    Task<PortablePostProcessingResult> ProcessAsync(
        string transcript,
        Mode mode,
        CancellationToken cancellationToken = default);
}

public sealed record TranscriptionWorkflowRequest(
    string? Language = null,
    string? ModeName = null,
    Guid? ModeId = null,
    Mode? SelectedMode = null,
    IReadOnlyList<string>? Vocabulary = null,
    ApplicationContextSnapshot? ApplicationContext = null,
    IReadOnlyList<PortableVocabularyReplacement>? VocabularyReplacements = null,
    IReadOnlyList<PortableVocabularyReplacement>? ModeVocabularyReplacements = null,
    SpeechOutputProcessingOptions? OutputOptions = null,
    bool PasteResultText = true,
    PortableCursorContext CursorContext = PortableCursorContext.Unknown)
{
    /// <summary>Freezes mutable mode and list state for one transcription operation.</summary>
    public TranscriptionWorkflowRequest Snapshot() => this with
    {
        SelectedMode = SelectedMode is null ? null : CloneMode(SelectedMode),
        Vocabulary = Vocabulary?.ToArray(),
        ApplicationContext = ApplicationContext is null ? null : ApplicationContext with { },
        VocabularyReplacements = VocabularyReplacements?.ToArray(),
        ModeVocabularyReplacements = ModeVocabularyReplacements?.ToArray(),
        OutputOptions = OutputOptions is null ? null : OutputOptions with { },
    };

    private static Mode CloneMode(Mode value) => new()
    {
        Id = value.Id, Name = value.Name, Preset = value.Preset,
        IsDefault = value.IsDefault, IsSystemProvided = value.IsSystemProvided, SortOrder = value.SortOrder,
        Language = value.Language, Model = value.Model, ModelType = value.ModelType,
        LocalEngine = value.LocalEngine, LocalParakeetModel = value.LocalParakeetModel,
        CloudProvider = value.CloudProvider, CloudTranscriptionModel = value.CloudTranscriptionModel,
        CloudTranscriptionDomain = value.CloudTranscriptionDomain, ProviderType = value.ProviderType,
        CloudAccuracyTier = value.CloudAccuracyTier, GeminiCustomPrompt = value.GeminiCustomPrompt,
        Punctuation = value.Punctuation, Capitalization = value.Capitalization,
        ProfanityFilter = value.ProfanityFilter, RemoveTrailingPeriod = value.RemoveTrailingPeriod,
        EnglishSpelling = value.EnglishSpelling, PostProcessingMode = value.PostProcessingMode,
        PostProcessingProvider = value.PostProcessingProvider, LanguageModel = value.LanguageModel,
        LocalPostProcessingModel = value.LocalPostProcessingModel, UserSystemPrompt = value.UserSystemPrompt,
        CustomInstructions = value.CustomInstructions, EnableScreenOCR = value.EnableScreenOCR,
        CloudPostProcessingModel = value.CloudPostProcessingModel,
        CustomVocabulary = value.CustomVocabulary?.ToList(),
        CreatedDate = value.CreatedDate, ModifiedDate = value.ModifiedDate,
        ForeignPlatformExtensions = value.ForeignPlatformExtensions,
    };
}

public static class TranscriptionTextDelivery
{
    public static async ValueTask<TextInjectionOutcome> DeliverAsync(
        ITextInjectionService textInjection,
        string text,
        bool pasteResultText,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(textInjection);
        ArgumentNullException.ThrowIfNull(text);
        if (pasteResultText)
            return await textInjection.InjectTranscriptAsync(text, cancellationToken).ConfigureAwait(false);
        var copied = await textInjection.CopyToClipboardAsync(text, cancellationToken).ConfigureAwait(false);
        return copied.IsSuccess ? TextInjectionOutcome.CopiedToClipboard : TextInjectionOutcome.Failed;
    }
}

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
        bool ownsDependencies = false)
    {
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _transcriber = transcriber ?? throw new ArgumentNullException(nameof(transcriber));
        _history = history ?? throw new ArgumentNullException(nameof(history));
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
        CancellationToken callerToken)
    {
        var transcript = new Transcript
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
            await _history.AddAsync(transcript, operation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested || callerToken.IsCancellationRequested)
        {
            return CompleteCancelled(operation, audioPath, ownsAudio);
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

        PortableTranscriptionResult result;
        try
        {
            result = await _transcriber.TranscribeAsync(audioPath, request, operation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested || callerToken.IsCancellationRequested)
        {
            return await CompleteCancelledAsync(transcript, operation, audioPath, ownsAudio).ConfigureAwait(false);
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
            return await CompleteCancelledAsync(transcript, operation, audioPath, ownsAudio).ConfigureAwait(false);
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
            var processingInput = rawText;
            string? postProcessedText = null;
            string? postProcessingProvider = null;
            if (ShouldPostProcess(request.SelectedMode) && _postProcessor is not null)
            {
                PortablePostProcessingResult postProcessing;
                try
                {
                    postProcessing = await _postProcessor.ProcessAsync(
                        rawText,
                        request.SelectedMode!,
                        request.ApplicationContext,
                        operation.Token).ConfigureAwait(false);
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
                {
                    processingInput = postProcessing.Text.Trim();
                    postProcessingProvider = postProcessing.Provider;
                }
            }

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
                return await CompleteCancelledAsync(transcript, operation, audioPath, ownsAudio).ConfigureAwait(false);

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
                    return await CompleteCancelledAsync(
                        transcript, operation, audioPath, ownsAudio).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Paste/clipboard failure never invalidates a successful
                    // transcription; Linux injection already degrades to copy.
                    injectionOutcome = TextInjectionOutcome.Failed;
                }
            }

            transcript.Text = finalText;
            transcript.TranscribedText = rawText;
            transcript.PostProcessedText = postProcessedText;
            transcript.Status = TranscriptStatus.Completed;
            transcript.TranscriptionProvider = result.Provider ?? _transcriber.Capability.DisplayName;
            transcript.PostProcessingProvider = postProcessingProvider;
            if (!await _history.UpdateAsync(transcript, operation.Token).ConfigureAwait(false))
                throw new InvalidOperationException("The processing transcript disappeared before completion.");
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
            };
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested || callerToken.IsCancellationRequested)
        {
            return await CompleteCancelledAsync(transcript, operation, audioPath, ownsAudio).ConfigureAwait(false);
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
