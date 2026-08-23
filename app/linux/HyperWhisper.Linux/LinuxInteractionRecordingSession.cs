using HyperWhisper.Data.Entities;
using HyperWhisper.Linux.Platform.Desktop;
using HyperWhisper.LiveStreaming;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.PortableApplication.Persistence;
using HyperWhisper.PortableApplication.Transcription;
using HyperWhisper.PortableApplication.ViewModels;
using HyperWhisper.SharedCore;
using HyperWhisper.Linux.Overlay;

namespace HyperWhisper.Linux;

internal sealed class LinuxInteractionRecordingSession : IInteractionRecordingSession
{
    private readonly ApplicationShellViewModel _viewModel;
    private readonly TranscriptionWorkflow _workflow;
    private readonly LinuxDesktopServices _services;
    private readonly LinuxContextCaptureCoordinator _contextCapture;
    private readonly LiveStreamingModeRouter _liveRouter;
    private readonly ITranscriptionPostProcessor _postProcessor;
    private readonly HistoryRepository _history;
    private readonly ILinuxRecordingOverlayFeedback _overlay;
    private ApplicationContextSnapshot? _context;
    private Mode? _mode;
    private Transcript? _liveTranscript;
    private IAudioEnvironmentSession? _audioEnvironment;
    private bool _streaming;
    private PortableCursorContext _cursorContext = PortableCursorContext.Unknown;

    public LinuxInteractionRecordingSession(
        ApplicationShellViewModel viewModel,
        TranscriptionWorkflow workflow,
        LinuxDesktopServices services,
        LinuxContextCaptureCoordinator contextCapture,
        ITranscriptionPostProcessor postProcessor,
        HistoryRepository history,
        ILinuxRecordingOverlayFeedback overlay)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _contextCapture = contextCapture ?? throw new ArgumentNullException(nameof(contextCapture));
        _postProcessor = postProcessor ?? throw new ArgumentNullException(nameof(postProcessor));
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _overlay = overlay ?? throw new ArgumentNullException(nameof(overlay));
        _liveRouter = new LiveStreamingModeRouter(new LinuxLiveStreamingCredentialSource(services.CredentialStore));
    }

    public bool IsActive => _streaming
        ? _services.LiveStreaming.IsRunning
        : _workflow.Snapshot.State is TranscriptionWorkflowState.Recording
            or TranscriptionWorkflowState.Stopping or TranscriptionWorkflowState.Transcribing
            or TranscriptionWorkflowState.Retrying;
    public bool IsStreaming => _streaming;

    public async ValueTask<PlatformResult> StartAsync(
        InteractionRecordingKind kind,
        CancellationToken cancellationToken = default)
    {
        try { return await StartCoreAsync(kind, cancellationToken); }
        catch (OperationCanceledException)
        {
            await RestoreAudioAsync();
            ClearSession();
            _overlay.Cancelled();
            throw;
        }
        catch { _overlay.Failed(LinuxRecordingOverlayError.RecordingFailed); throw; }
    }

    private async ValueTask<PlatformResult> StartCoreAsync(
        InteractionRecordingKind kind,
        CancellationToken cancellationToken)
    {
        if (kind == InteractionRecordingKind.Streaming && !_viewModel.Settings.StreamingEnabled)
            return PlatformResult.Failure("interaction.streaming_disabled", "Enable live transcription before starting a streaming session.");
        if (IsActive) return PlatformResult.Failure("interaction.already_recording", "A transcription is already active.");
        _mode = _viewModel.Modes.Selected ?? _viewModel.Modes.Items.FirstOrDefault(item => item.IsDefault)
            ?? _viewModel.Modes.Items.FirstOrDefault();
        if (_mode is null)
        {
            _overlay.Failed(LinuxRecordingOverlayError.RecordingFailed);
            return PlatformResult.Failure("interaction.mode_missing", "Create a transcription mode before recording.");
        }

        _cursorContext = MapCursorContext(
            await _services.InsertionContext.GetCursorContextAsync(cancellationToken));

        var captured = await _contextCapture.CaptureAsync(_mode.EnableScreenOCR, cancellationToken: cancellationToken);
        _context = captured.Snapshot;
        if (captured.OcrFailure is not null)
            _viewModel.Status.Failure(captured.OcrFailure.Code, captured.OcrFailure.Message);

        _overlay.RecordingStarted(LinuxOverlayModeLabel.Create(_mode.Name));
        var deviceId = _viewModel.Recording?.SelectedAudioDevice?.Id ?? "default";
        PrepareAudio(deviceId);
        _streaming = kind == InteractionRecordingKind.Streaming;
        PlatformResult started;
        if (_streaming)
            started = await StartStreamingAsync(deviceId, cancellationToken);
        else
        {
            var result = await _workflow.StartRecordingAsync(cancellationToken);
            started = result.IsSuccess
                ? PlatformResult.Success()
                : PlatformResult.Failure("workflow.start_failed", result.Failure?.Message ?? "Recording could not start.");
        }

        if (started.IsFailure)
        {
            await RestoreAudioAsync();
            _streaming = false;
            _context = null;
            _mode = null;
            _overlay.Failed(LinuxRecordingOverlayErrorMapper.FromCode(started.Error?.Code, transcription: false));
            return started;
        }
        if (_viewModel.Settings.EnableSoundEffects) _ = _services.SoundEffects.Play(SoundEffect.RecordingStarted);
        _viewModel.Status.Success(_streaming ? "Live transcription recording…" : "Recording…");
        return PlatformResult.Success();
    }

    private async Task<PlatformResult> StartStreamingAsync(string deviceId, CancellationToken cancellationToken)
    {
        var identity = _services.DeviceIdentity.GetDeviceIdentity();
        if (string.Equals(_viewModel.Settings.StreamingProvider, "hyperwhisper", StringComparison.Ordinal)
            && identity.IsFailure)
            return PlatformResult.Failure(identity.Error!.Code, identity.Error.Message);
        var resolved = await _liveRouter.ResolveAsync(new LiveStreamingModeSettings(
            _mode!.Id.ToString("D"),
            Enabled: true,
            _viewModel.Settings.StreamingProvider,
            deviceId,
            _viewModel.Settings.StreamingLanguage,
            _mode.CustomVocabulary,
            LinuxLiveStreamingSettingsMapper.ModelForProvider(
                _viewModel.Settings.StreamingProvider, _viewModel.Settings.StreamingModel),
            _viewModel.Settings.StreamingFastFormatting,
            identity.Value?.Id),
            _viewModel.Vocabulary.Items.Select(item => item.Word).ToArray(), cancellationToken);
        if (resolved.IsFailure) return PlatformResult.Failure(resolved.Error!.Code, resolved.Error.Message);
        var started = _services.LiveStreaming.Start(new LiveStreamingSessionRequest(
            resolved.Value!.Config, resolved.Value.AudioDeviceId), cancellationToken);
        if (started.IsFailure) return started;

        _liveTranscript = new Transcript
        {
            Status = TranscriptStatus.Processing,
            Text = "Live transcription in progress",
            Date = DateTime.UtcNow,
            Mode = _mode.Name,
            ModeId = _mode.Id,
            TranscriptionProvider = resolved.Value.Config.Provider.ToString(),
        };
        try { await _history.AddAsync(_liveTranscript, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _ = await _services.LiveStreaming.CancelAsync(CancellationToken.None);
            _liveTranscript = null;
            throw;
        }
        catch
        {
            _ = await _services.LiveStreaming.CancelAsync(CancellationToken.None);
            _liveTranscript = null;
            return PlatformResult.Failure("streaming.persistence_failed", "The live transcription history row could not be created.");
        }
        return PlatformResult.Success();
    }

    public async ValueTask<InteractionStopOutcome> StopAsync(CancellationToken cancellationToken = default)
    {
        if (!IsActive) return new(PlatformResult.Failure("interaction.not_recording", "No transcription is active."), false);
        _overlay.Transcribing();
        try
        {
            if (_viewModel.Settings.EnableSoundEffects) _ = _services.SoundEffects.Play(SoundEffect.RecordingStopped);
            InteractionStopOutcome outcome;
            if (_streaming) outcome = await StopStreamingAsync(cancellationToken);
            else
            {
                var result = await _workflow.StopAndTranscribeAsync(BuildRequest(), cancellationToken);
                var status = result.IsSuccess ? PlatformResult.Success()
                    : PlatformResult.Failure("workflow.transcription_failed", result.Failure?.Message ?? "Transcription failed.");
                outcome = InteractionStopOutcome.FromInjection(
                    status, result.InjectionOutcome, _viewModel.Settings.RestoreClipboardAfterPaste);
            }
            if (outcome.Result.IsSuccess) _overlay.Completed();
            else _overlay.Failed(LinuxRecordingOverlayErrorMapper.FromCode(
                outcome.Result.Error?.Code, transcription: true));
            return outcome;
        }
        catch (OperationCanceledException) { _overlay.Cancelled(); throw; }
        catch { _overlay.Failed(LinuxRecordingOverlayError.TranscriptionFailed); throw; }
        finally
        {
            await RestoreAudioAsync();
            ClearSession();
        }
    }

    private async ValueTask<InteractionStopOutcome> StopStreamingAsync(CancellationToken cancellationToken)
    {
        var outcome = await _services.LiveStreaming.StopAsync(cancellationToken);
        var transcript = _liveTranscript;
        if (!outcome.IsSuccess || string.IsNullOrWhiteSpace(outcome.Transcription.Transcript))
        {
            if (transcript is not null)
            {
                transcript.Status = TranscriptStatus.Failed;
                transcript.Text = outcome.CaptureFailure?.Message ?? outcome.Transcription.Failure?.Message ?? "Live transcription failed";
                transcript.FailedReason = transcript.Text;
                transcript.Duration = outcome.CaptureDuration.TotalSeconds;
                _ = await _history.UpdateAsync(transcript, cancellationToken);
            }
            await RefreshHistoryAsync(cancellationToken);
            return new(PlatformResult.Failure("streaming.transcription_failed",
                outcome.CaptureFailure?.Message ?? outcome.Transcription.Failure?.Message ?? "Live transcription failed."), false);
        }

        if (transcript is null || _mode is null)
            return new(PlatformResult.Failure("streaming.persistence_failed", "The live transcription history row is unavailable."), false);
        transcript.Duration = outcome.CaptureDuration.TotalSeconds;
        var finalization = await LinuxLiveTranscriptionFinalizer.FinalizeAndPersistAsync(
            outcome.Transcription.Transcript, transcript, _mode, _context, _postProcessor,
            _services.TextInjection, _history, BuildRequest(), cancellationToken);
        if (finalization.Result.IsFailure) return new(finalization.Result, false);
        await RefreshHistoryAsync(cancellationToken);
        _viewModel.Status.Success(finalization.InjectionOutcome switch
        {
            TextInjectionOutcome.Pasted => "Live transcription pasted and saved",
            TextInjectionOutcome.CopiedToClipboard => "Live transcription copied and saved",
            TextInjectionOutcome.SecureFieldSkipped => "Live transcription saved; secure field was not modified",
            _ => "Live transcription saved, but text injection failed",
        });
        return InteractionStopOutcome.FromInjection(
            PlatformResult.Success(), finalization.InjectionOutcome, _viewModel.Settings.RestoreClipboardAfterPaste);
    }

    public async ValueTask CancelAsync(CancellationToken cancellationToken = default)
    {
        _overlay.Cancelled();
        try
        {
            if (_streaming)
            {
                if (_services.LiveStreaming.IsRunning)
                    _ = await _services.LiveStreaming.CancelAsync(cancellationToken);
                if (_liveTranscript is not null) _ = await _history.DeleteAsync(_liveTranscript.Id, cancellationToken);
                await RefreshHistoryAsync(cancellationToken);
            }
            else await _workflow.CancelAsync();
            _viewModel.Status.Success("Recording cancelled");
        }
        finally
        {
            await RestoreAudioAsync();
            ClearSession();
        }
    }

    private TranscriptionWorkflowRequest BuildRequest() =>
        _viewModel.CreateTranscriptionRequest(_mode, _context, _cursorContext);

    internal static PortableCursorContext MapCursorContext(InsertionCursorContext context) => context switch
    {
        InsertionCursorContext.StartOfSentence => PortableCursorContext.StartOfSentence,
        InsertionCursorContext.MidSentence => PortableCursorContext.MidSentence,
        _ => PortableCursorContext.Unknown,
    };

    private void PrepareAudio(string deviceId)
    {
        _services.MicrophoneKeepWarm.SuspendForRecording();
        if (_viewModel.Settings.AutoIncreaseMicVolume) _ = _services.MicrophoneVolume.BoostIfNeeded(deviceId);
        var policy = _viewModel.Settings.AudioEnvironmentPolicy switch
        {
            "duck" => AudioEnvironmentPolicy.DuckOtherAudio,
            "mute" => AudioEnvironmentPolicy.MuteOtherAudio,
            _ => AudioEnvironmentPolicy.Unchanged,
        };
        var environment = _services.AudioEnvironment.PrepareForRecording(policy, TimeSpan.FromMilliseconds(500));
        _audioEnvironment = environment.IsSuccess ? environment.Value : null;
    }

    private async ValueTask RestoreAudioAsync()
    {
        var environment = _audioEnvironment;
        _audioEnvironment = null;
        await LinuxRecordingAudioRestorer.RestoreAsync(
            _services.MicrophoneVolume, environment, _services.MicrophoneKeepWarm,
            _viewModel.Recording?.SelectedAudioDevice?.Id);
    }

    private async Task RefreshHistoryAsync(CancellationToken cancellationToken)
    {
        await Task.WhenAll(_viewModel.History.RefreshAsync(cancellationToken), _viewModel.Home.RefreshAsync(cancellationToken));
    }

    private void ClearSession()
    {
        _streaming = false;
        _liveTranscript = null;
        _context = null;
        _mode = null;
        _cursorContext = PortableCursorContext.Unknown;
    }
}
