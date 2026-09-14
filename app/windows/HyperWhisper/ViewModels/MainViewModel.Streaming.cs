using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HyperWhisper.Data.Entities;
using HyperWhisper.FileTranscription;
using HyperWhisper.Models;
using HyperWhisper.Localization;
using HyperWhisper.Services;
using HyperWhisper.Services.Streaming;
using HyperWhisper.Services.Transcription;
using HyperWhisper.Utilities;
using HyperWhisper.ViewModels.Base;
using System.Collections.ObjectModel;
using System.Windows.Media;

namespace HyperWhisper.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private bool IsStreamingActive() => _isStreamingSession || _isStreamingStarting || _isStoppingStreaming;

    private bool IsActiveStreamingGeneration(int generation) =>
        _isStreamingSession && generation == _streamingSessionGeneration;

    private string GetStreamingStartupFailureMessage() =>
        !string.IsNullOrWhiteSpace(_streamingFailureMessage)
            ? _streamingFailureMessage
            : "Streaming transcription connection could not be started.";

    private async Task StartStreamingRecordingAsync()
    {
        // The second capture entry point; see StartRecordingAsync for why the
        // guard is here rather than on the shortcut handler.
        if (OnboardingSession.IsActive)
        {
            LoggingService.Info("StartStreamingRecordingAsync: Ignored while the onboarding window is open");
            return;
        }

        if (_isStreamingStarting || _isStreamingSession)
            return;

        if (SelectedAudioDevice == null)
        {
            LoggingService.Warn($"StartStreamingRecordingAsync: No audio device - DeviceCount={AudioDevices.Count}");
            ShowErrorToastRequested?.Invoke(this, new ErrorToastEventArgs(
                Loc.S("errors.noMicrophone"),
                showSettingsButton: false));
            return;
        }

        // No local trial gate — local transcription is unlimited (open source).

        var vocabulary = _vocabularyService.GetVocabularyWords(100);
        var clientResult = StreamingTranscriptionSessionFactory.Create(vocabulary);
        if (clientResult.IsFailure)
        {
            LoggingService.Warn($"StartStreamingRecordingAsync: {clientResult.Error}");
            ShowErrorToastRequested?.Invoke(this, new ErrorToastEventArgs(
                clientResult.Error ?? Loc.S("errors.recordingStartFailed"),
                showSettingsButton: true,
                openApiKeysManager: true));
            return;
        }

        _pasteService?.CaptureForegroundWindow();
        _capturedApplicationContext = ApplicationContextService.Instance.GatherContext();
        _streamingFailureMessage = null;
        _streamingPastedFinalSegment = false;
        _streamingTargetLost = false;
        _streamingDurationLimitReached = false;
        _streamingLastPasteResult = SmartPasteResult.Failed;
        _streamingPendingFinalFallbackText = string.Empty;

        _streamingClient = clientResult.Value!;
        _streamingClient.ErrorReceived += OnStreamingErrorReceived;
        _streamingClient.FinalTranscriptSegmentReceived += OnStreamingFinalTranscriptSegmentReceived;
        _streamingClient.WarningReceived += OnStreamingWarningReceived;
        _streamingClient.SessionCompleted += OnStreamingSessionCompleted;
        _streamingClient.StateChanged += OnStreamingConnectionStateChanged;
        _isStreamingStarting = true;
        _streamingStartCancelledByUser = false;
        _streamingStartCts = new CancellationTokenSource(StreamingConnectionTimeout);
        _streamingSessionGeneration++;

        var providerName = GetStreamingProviderDisplayName();
        SentryService.AddBreadcrumb(
            "streaming_start_requested",
            "audio.streaming",
            data: new Dictionary<string, string> { ["provider"] = providerName });

        _streamingAudioCapture = new StreamingAudioCapture();
        _streamingAudioCapture.AudioChunkAvailable += OnStreamingAudioChunkAvailable;
        _streamingAudioCapture.AudioLevelChanged += _audioLevelHandler;

        AudioEnvironmentService.AudioEnvironmentRestoreClaim? audioRestoreClaim = null;

        try
        {
            ShowStreamingOverlayRequested?.Invoke(this, providerName);
            _pasteService?.StartRecordingSession();
            SuspendMicrophoneKeepWarm();

            if (SettingsService.Instance.AutoIncreaseMicVolume)
                _recorderService.BoostMicVolume(SelectedAudioDevice.DeviceNumber);

            audioRestoreClaim = AudioEnvironmentService.Instance.ClaimRestoreOwnershipForRecording();
            _audioEnvironmentState = audioRestoreClaim.InheritedRestoreState;
            var started = await _streamingClient.StartAsync(_streamingStartCts.Token);
            // StartAsync's own internal race-closing check (see StreamingTranscriptionClient) can
            // still lose to a terminal close/error landing on the receive-loop thread in the
            // instant right after it decided to return true - re-check State here too so a
            // dead-on-arrival connection never gets marked as an actively recording session.
            if (!started || _streamingClient.State == StreamingConnectionState.Error)
                throw new InvalidOperationException(GetStreamingStartupFailureMessage());

            _streamingAudioCapture.Start(SelectedAudioDevice.DeviceNumber, _streamingClient.AudioSampleRate);
        }
        catch (OperationCanceledException)
        {
            var failureMessage = _streamingFailureMessage;
            LoggingService.Warn(_streamingStartCancelledByUser
                ? "StartStreamingRecordingAsync: Streaming start cancelled by user"
                : "StartStreamingRecordingAsync: Streaming connection timed out");
            SentryService.AddBreadcrumb(
                _streamingStartCancelledByUser ? "streaming_start_cancelled" : "streaming_start_timeout",
                "audio.streaming",
                data: new Dictionary<string, string> { ["provider"] = providerName });
            HideOverlayRequested?.Invoke(this, EventArgs.Empty);
            await CleanupStreamingSessionAsync();
            CleanupFailedRecordingStart();

            if (!_streamingStartCancelledByUser)
            {
                var message = !string.IsNullOrWhiteSpace(failureMessage)
                    ? failureMessage
                    : "Streaming connection timed out. Check your internet connection and try again.";
                ShowErrorToastRequested?.Invoke(this, new ErrorToastEventArgs(
                    message,
                    showSettingsButton: false));
                StatusText = Loc.S("status.failed", message);
            }
            return;
        }
        catch (Exception ex)
        {
            var failureMessage = _streamingFailureMessage;
            LoggingService.Error($"StartStreamingRecordingAsync: Recording start failed - {ex.Message}", ex);
            SentryService.AddBreadcrumb(
                "streaming_start_failed",
                "audio.streaming",
                data: new Dictionary<string, string> { ["provider"] = providerName, ["errorType"] = ex.GetType().Name });
            HideOverlayRequested?.Invoke(this, EventArgs.Empty);
            await CleanupStreamingSessionAsync();
            CleanupFailedRecordingStart();

            var message = !string.IsNullOrWhiteSpace(failureMessage)
                ? failureMessage
                : !string.IsNullOrWhiteSpace(ex.Message)
                    ? ex.Message
                    : Loc.S("errors.recordingStartFailed");
            ShowErrorToastRequested?.Invoke(this, new ErrorToastEventArgs(
                message,
                showSettingsButton: false));
            StatusText = Loc.S("status.failed", message);
            return;
        }
        finally
        {
            _isStreamingStarting = false;
            _streamingStartCancelledByUser = false;
            _streamingStartCts?.Dispose();
            _streamingStartCts = null;
        }

        _isStreamingSession = true;
        IsRecording = true;
        SoundEffectsService.Instance.PlayStartSound();
        _audioEnvironmentState = AudioEnvironmentService.Instance.PrepareForRecording(audioRestoreClaim!);
        SentryService.AddBreadcrumb(
            "streaming_started",
            "audio.streaming",
            data: new Dictionary<string, string> { ["provider"] = providerName });
        RecordingDuration = TimeSpan.Zero;
        _durationTimer?.Dispose();
        _durationTimer = new System.Timers.Timer(100);
        _durationTimer.Elapsed += (s, e) =>
        {
            RecordingDuration = _streamingAudioCapture?.Duration ?? TimeSpan.Zero;
            CheckStreamingTargetAvailability();
            CheckStreamingDurationLimit();
        };
        _durationTimer.Start();
    }

    private async Task StopStreamingRecordingAsync()
    {
        if (_isStoppingStreaming)
        {
            LoggingService.Debug("StopStreamingRecordingAsync: stop already in progress; ignoring duplicate request");
            return;
        }

        _isStoppingStreaming = true;
        LoggingService.LogPerformanceMarker("StreamingTranscriptionFlow", "StopStreamingRecordingAsync invoked");
        SentryService.AddBreadcrumb(
            "streaming_stop_requested",
            "audio.streaming",
            data: new Dictionary<string, string> { ["provider"] = GetStreamingProviderDisplayName() });
        _durationTimer?.Stop();
        _hotkeyBlocked = true;
        IsTranscribing = true;
        ShowTranscribingRequested?.Invoke(this, EventArgs.Empty);

        Transcript? transcript = null;

        try
        {
            var durationSeconds = RecordingDuration.TotalSeconds;

            _streamingAudioCapture?.Stop();
            _recorderService.RestoreMicVolume();
            RestoreAudioEnvironment();
            ResumeMicrophoneKeepWarm();
            IsRecording = false;

            var finalText = _streamingClient != null
                ? await _streamingClient.StopAsync()
                : string.Empty;

            finalText = TranscriptionTextProcessing.FinalizeStreamingText(finalText);
            if (string.IsNullOrWhiteSpace(finalText))
            {
                if (!string.IsNullOrWhiteSpace(_streamingFailureMessage))
                {
                    throw new InvalidOperationException(_streamingFailureMessage);
                }

                throw new TranscriptionException(
                    TranscriptionErrorCode.NoSpeechDetected,
                    Loc.S("errors.noSpeechDetected"),
                    GetStreamingProviderDisplayName());
            }

            transcript = HistoryService.Instance.CreateProcessingTranscript(
                durationSeconds,
                SelectedMode?.Name,
                audioFilePath: null);

            transcript.Text = finalText;
            transcript.TranscribedText = finalText;
            transcript.Status = TranscriptStatus.Completed;
            transcript.TranscriptionProvider = GetStreamingProviderDisplayName();

            if (!string.IsNullOrWhiteSpace(_streamingFailureMessage) && !_streamingDurationLimitReached)
            {
                transcript.Status = TranscriptStatus.Failed;
                transcript.FailedReason = _streamingFailureMessage;
                HistoryService.Instance.UpdateTranscript(transcript);

                ShowErrorToastRequested?.Invoke(this, new ErrorToastEventArgs(
                    _streamingFailureMessage,
                    showSettingsButton: false));
                StatusText = Loc.S("status.failed", _streamingFailureMessage);
                return;
            }

            // No local usage recording — local transcription is unlimited (open source).

            var textToProcess = finalText;

            var pasteResult = _streamingLastPasteResult;
            var pendingFallbackText = TranscriptionTextProcessing.FinalizeStreamingText(_streamingPendingFinalFallbackText);
            if (!SettingsService.Instance.AutoPasteEnabled)
            {
                var spacedText = TranscriptionTextProcessing.AppendTrailingSpace(textToProcess, _settingsService.StreamingLanguage);
                var copied = _pasteService?.CopyToClipboard(spacedText) ?? false;
                pasteResult = copied ? SmartPasteResult.CopiedToClipboard : SmartPasteResult.Failed;
                LoggingService.Debug(copied
                    ? "MainViewModel: Auto-paste disabled, streaming text copied to clipboard only"
                    : "MainViewModel: Auto-paste disabled and the clipboard copy was refused; streaming text was not delivered");

                if (!copied)
                    ReportUndeliveredTranscript();
            }
            else if (!string.IsNullOrWhiteSpace(pendingFallbackText))
            {
                var targetAvailable = !_streamingTargetLost && _pasteService?.IsCapturedTargetAvailable() != false;
                pasteResult = targetAvailable
                    ? PasteStreamingFinalSegment(pendingFallbackText)
                    : SmartPasteResult.Failed;

                if (pasteResult == SmartPasteResult.Pasted)
                {
                    _streamingPendingFinalFallbackText = string.Empty;
                }
                else
                {
                    var spacedText = TranscriptionTextProcessing.AppendTrailingSpace(textToProcess, _settingsService.StreamingLanguage);
                    var copied = _pasteService?.CopyToClipboard(spacedText) ?? false;
                    if (pasteResult != SmartPasteResult.SecureFieldSkipped)
                    {
                        pasteResult = copied ? SmartPasteResult.CopiedToClipboard : SmartPasteResult.Failed;
                        LoggingService.Warn(copied
                            ? "MainViewModel: Streaming pending final segment paste failed; copied full transcript to clipboard"
                            : "MainViewModel: Streaming pending final segment paste failed and the clipboard copy was refused; text was not delivered");

                        if (!copied)
                            ReportUndeliveredTranscript();
                    }
                    else
                    {
                        LoggingService.Info("MainViewModel: Streaming pending final segment hit secure field; full transcript left on clipboard for manual paste");
                    }
                }
            }
            else if (!_streamingPastedFinalSegment && !_streamingTargetLost)
            {
                pasteResult = PasteStreamingFinalSegment(textToProcess);
            }

            // SecureFieldSkipped intentionally left the transcription on the clipboard
            // for manual paste — restoring the old clipboard would wipe it. Skip restore.
            if (pasteResult != SmartPasteResult.SecureFieldSkipped)
            {
                _pasteService?.ScheduleClipboardRestore();
            }
            HistoryService.Instance.UpdateTranscript(transcript);
            SentryService.AddBreadcrumb(
                "streaming_saved",
                "audio.streaming",
                data: new Dictionary<string, string>
                {
                    ["provider"] = GetStreamingProviderDisplayName(),
                    ["durationSeconds"] = ((int)durationSeconds).ToString()
                });

            switch (pasteResult)
            {
                case SmartPasteResult.Pasted:
                    ShowSuccessRequested?.Invoke(this, EventArgs.Empty);
                    await Task.Delay(400);
                    break;
                case SmartPasteResult.SecureFieldSkipped:
                case SmartPasteResult.CopiedToClipboard:
                    ShowCopiedRequested?.Invoke(this, EventArgs.Empty);
                    await Task.Delay(500);
                    break;
            }
        }
        catch (Exception ex)
        {
            var failureWritten = transcript == null || MarkTranscriptAsGenericFailure(transcript, ex);

            // If the guard no-op'd because the transcript was already persisted as
            // Completed (some other path already wrote a result), don't show a
            // misleading "transcription failed" toast/status on top of it.
            if (failureWritten)
            {
                var isCredentialError = ex is TranscriptionException { Code: TranscriptionErrorCode.ApiKeyMissing or TranscriptionErrorCode.Unauthorized };
                // CloudAccountRequired routes to the HW Cloud settings page (where
                // the account key is entered), NOT the BYOK API-keys manager.
                var isCloudAccountError = ex is TranscriptionException { Code: TranscriptionErrorCode.CloudAccountRequired };
                ShowErrorToastRequested?.Invoke(this, new ErrorToastEventArgs(
                    ex is TranscriptionException txEx ? txEx.GetUserMessage() : Loc.S("errors.transcriptionFailed", ex.Message),
                    showSettingsButton: isCredentialError || isCloudAccountError,
                    settingsSection: isCloudAccountError ? "Cloud" : null,
                    openApiKeysManager: isCredentialError));

                StatusText = Loc.S("status.failed", ex.Message);
            }
        }
        finally
        {
            // SAFETY NET: Ensure the transcript is never left stuck in Processing.
            // If any code path above returned or threw without writing a terminal
            // status, flip it to Failed here so the History row doesn't spin forever.
            // See tasks/windows/phils-feedback/05-processing-audio-stuck-state.md
            EnsureTranscriptTerminalStatus(transcript);

            HideOverlayRequested?.Invoke(this, EventArgs.Empty);
            try
            {
                await CleanupStreamingSessionAsync();
            }
            finally
            {
                _hotkeyBlocked = false;
                IsTranscribing = false;
                _toggleShortcutHeld = false;
                _pushToTalkMonitor.Reset();
                _shortcutService.ResetKeyboardState();
                _pasteService?.EndRecordingSession();
                _isStoppingStreaming = false;
            }
        }
    }

    private void OnStreamingFinalTranscriptSegmentReceived(string segment)
    {
        var generation = _streamingSessionGeneration;
        OnStreamingFinalTranscriptSegmentReceived(segment, generation);
    }

    private void OnStreamingFinalTranscriptSegmentReceived(string segment, int generation)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => OnStreamingFinalTranscriptSegmentReceived(segment, generation));
            return;
        }

        if (!IsActiveStreamingGeneration(generation))
            return;

        if (!SettingsService.Instance.AutoPasteEnabled)
            return;

        if (!CheckStreamingTargetAvailability())
            return;

        _streamingLastPasteResult = PasteStreamingFinalSegment(segment);
        if (_streamingLastPasteResult == SmartPasteResult.Pasted)
        {
            _streamingPastedFinalSegment = true;
        }
        else if (_streamingLastPasteResult == SmartPasteResult.Failed)
        {
            AppendStreamingPendingFallback(segment);
        }
    }

    private void AppendStreamingPendingFallback(string segment)
    {
        var cleaned = TranscriptionTextProcessing.FinalizeStreamingText(segment);
        if (string.IsNullOrWhiteSpace(cleaned))
            return;

        _streamingPendingFinalFallbackText = string.IsNullOrWhiteSpace(_streamingPendingFinalFallbackText)
            ? cleaned
            : TranscriptionTextProcessing.FinalizeStreamingText($"{_streamingPendingFinalFallbackText} {cleaned}");
    }

    private bool CheckStreamingTargetAvailability()
    {
        if (!_isStreamingSession ||
            _streamingTargetLost ||
            !SettingsService.Instance.AutoPasteEnabled ||
            _pasteService?.IsCapturedTargetAvailable() != false)
        {
            return true;
        }

        _streamingTargetLost = true;
        LoggingService.Warn("Streaming target window lost; stopping streaming session");
        SentryService.AddBreadcrumb("streaming_target_lost", "audio.streaming");

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(async () => await StopStreamingRecordingAsync());
        }
        else
        {
            _ = StopStreamingRecordingAsync();
        }

        return false;
    }

    /// <summary>
    /// Run <paramref name="action"/> on the UI dispatcher: posted via BeginInvoke
    /// from background threads, invoked inline when already on the UI thread (or
    /// when no dispatcher exists).
    /// </summary>
    private static void DispatchToUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(action);
        }
        else
        {
            action();
        }
    }

    private void CheckRecordingDurationLimit()
    {
        if (!IsRecording ||
            _isStreamingSession ||
            _recordingDurationLimitReached ||
            RecordingDuration < EffectiveMaxRecordingDuration)
        {
            return;
        }

        _recordingDurationLimitReached = true;
        LoggingService.Warn("Recording duration limit reached; auto-stopping session");
        SentryService.AddBreadcrumb("recording_duration_limit_reached", "audio.recording");

        // The async helper re-checks recording state once on the UI thread —
        // the session may have stopped between this tick and the dispatch.
        DispatchToUi(() => _ = AutoStopRecordingAfterDurationLimitAsync());
    }

    private async Task AutoStopRecordingAfterDurationLimitAsync()
    {
        if (!IsRecording || _isStreamingSession || IsTranscribing)
        {
            return;
        }

        ShowErrorToastRequested?.Invoke(this, new ErrorToastEventArgs(
            Loc.S("errors.recordingDurationLimit"),
            showSettingsButton: false));
        await StopRecordingAndTranscribeAsync();
    }

    private void CheckStreamingDurationLimit()
    {
        if (!_isStreamingSession ||
            _streamingDurationLimitReached ||
            RecordingDuration < EffectiveMaxRecordingDuration)
        {
            return;
        }

        _streamingDurationLimitReached = true;
        // Set BEFORE the dispatch: the stop path reads this field to explain the
        // session end, and the dispatched toast must observe it too.
        _streamingFailureMessage = Loc.S("errors.streamingDurationLimit");
        LoggingService.Warn("Streaming duration limit reached; stopping session");
        SentryService.AddBreadcrumb("streaming_duration_limit_reached", "audio.streaming");

        DispatchToUi(() =>
        {
            // Toast first, then stop — the stop path clears session state the
            // toast message derives from.
            ShowErrorToastRequested?.Invoke(this, new ErrorToastEventArgs(
                _streamingFailureMessage,
                showSettingsButton: false));
            _ = StopStreamingRecordingAsync();
        });
    }

    private SmartPasteResult PasteStreamingFinalSegment(string segment)
    {
        var spacedText = TranscriptionTextProcessing.AppendTrailingSpace(segment, _settingsService.StreamingLanguage);
        return _pasteService?.SmartPaste(spacedText) ?? SmartPasteResult.Failed;
    }

    private async void OnStreamingAudioChunkAvailable(byte[] chunk)
    {
        try
        {
            if (_streamingClient != null)
            {
                await _streamingClient.SendAudioAsync(chunk);
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"Streaming audio send failed: {ex.Message}");
        }
    }

    private void OnStreamingErrorReceived(string message)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => OnStreamingErrorReceived(message));
            return;
        }

        _streamingFailureMessage = message;
        StatusText = Loc.S("status.failed", message);
        LoggingService.Error($"Streaming provider error: {message}");
        SentryService.AddBreadcrumb("streaming_provider_error", "audio.streaming");

        if (_isStreamingSession && IsRecording)
        {
            _ = StopStreamingRecordingAsync();
        }
    }

    private void OnStreamingWarningReceived(string message)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => OnStreamingWarningReceived(message));
            return;
        }

        LoggingService.Warn($"Streaming warning: {message}");
        SentryService.AddBreadcrumb("streaming_provider_warning", "audio.streaming");
        ShowErrorToastRequested?.Invoke(this, new ErrorToastEventArgs(
            message,
            showSettingsButton: false));
    }

    private void OnStreamingConnectionStateChanged(StreamingConnectionState state)
    {
        StreamingConnectionStateChanged?.Invoke(this, state);
    }

    private void CancelStreamingStart()
    {
        if (!_isStreamingStarting)
            return;

        _streamingStartCancelledByUser = true;
        LoggingService.Info("Cancelling streaming connection attempt");
        SentryService.AddBreadcrumb("streaming_start_cancel_requested", "audio.streaming");
        _streamingStartCts?.Cancel();
    }

    private void OnStreamingSessionCompleted(double durationSeconds, double creditsUsed)
    {
        LoggingService.Info($"Streaming session complete: {durationSeconds:F2}s, {creditsUsed:F2} credits");
        SentryService.AddBreadcrumb(
            "streaming_session_complete",
            "audio.streaming",
            data: new Dictionary<string, string>
            {
                ["provider"] = GetStreamingProviderDisplayName(),
                ["durationSeconds"] = durationSeconds.ToString("F2"),
                ["creditsUsed"] = creditsUsed.ToString("F2")
            });

        var provider = StreamingTranscriptionProviderExtensions.FromStorageValue(_settingsService.StreamingProvider);
        if (provider != StreamingTranscriptionProvider.HyperWhisperCloud)
            return;

        var cloudManager = HyperWhisperCloudManager.Instance;
        cloudManager.InvalidateCache();
        _ = RefreshHyperWhisperCloudCreditsAfterStreamingAsync();
    }

    private static async Task RefreshHyperWhisperCloudCreditsAfterStreamingAsync()
    {
        try
        {
            await HyperWhisperCloudManager.Instance.RefreshCreditsAsync();
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"Streaming credit refresh failed: {ex.Message}");
        }
    }

    private async Task CleanupStreamingSessionAsync()
    {
        _isStreamingSession = false;
        _streamingSessionGeneration++;
        _streamingFailureMessage = null;
        _streamingTargetLost = false;

        if (_streamingAudioCapture != null)
        {
            _streamingAudioCapture.AudioChunkAvailable -= OnStreamingAudioChunkAvailable;
            _streamingAudioCapture.AudioLevelChanged -= _audioLevelHandler;
            _streamingAudioCapture.Dispose();
            _streamingAudioCapture = null;
        }

        if (_streamingClient != null)
        {
            _streamingClient.ErrorReceived -= OnStreamingErrorReceived;
            _streamingClient.FinalTranscriptSegmentReceived -= OnStreamingFinalTranscriptSegmentReceived;
            _streamingClient.WarningReceived -= OnStreamingWarningReceived;
            _streamingClient.SessionCompleted -= OnStreamingSessionCompleted;
            _streamingClient.StateChanged -= OnStreamingConnectionStateChanged;
            await _streamingClient.DisposeAsync();
            _streamingClient = null;
        }
    }

    private string GetStreamingProviderDisplayName()
    {
        var provider = StreamingTranscriptionProviderExtensions.FromStorageValue(_settingsService.StreamingProvider);
        return $"{provider.DisplayName()} (Streaming)";
    }
}
