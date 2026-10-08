using System.IO;
using System.Threading;
using CommunityToolkit.Mvvm.Input;
using HyperWhisper.Data.Entities;
using HyperWhisper.FileTranscription;
using HyperWhisper.Localization;
using HyperWhisper.Models;
using HyperWhisper.Services;
using HyperWhisper.Services.Transcription;
using HyperWhisper.Utilities;

namespace HyperWhisper.ViewModels;

public partial class MainViewModel
{
    // =========================================================================
    // FILE TRANSCRIPTION
    // =========================================================================

    /// <summary>
    /// Opens a file dialog and transcribes the selected audio file.
    /// Implements file transcription with the same provider routing as live recording.
    /// </summary>
    [RelayCommand]
    private async Task TranscribeFile()
    {
        if (SelectedMode == null)
        {
            LoggingService.Warn($"TranscribeFile: No mode selected - ModeCount={Modes.Count}");
            ShowErrorToastRequested?.Invoke(this, new ErrorToastEventArgs(
                Loc.S("errors.noModeSelected"),
                showSettingsButton: false));
            return;
        }

        await TranscribeFileWithModeAsync(SelectedMode);
    }

    public async Task TranscribeFileWithModeAsync(Mode mode)
    {
        if (!CanStartFileTranscription())
        {
            return;
        }

        // Open file dialog
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.S("file.transcribe.dialogTitle"),
            Filter = FileTranscriptionService.FileFilter,
            Multiselect = false
        };

        if (dialog.ShowDialog() == true)
        {
            await TranscribeFileAsync(dialog.FileName, mode);
        }
    }

    /// <summary>
    /// Transcribes an audio file using the selected mode and provider.
    ///
    /// FLOW:
    /// 1. Validate file and mode selection
    /// 2. Check file size limits per provider
    /// 3. Show transcribing overlay
    /// 4. Convert file to 16kHz mono WAV (Whisper format)
    /// 5. Get audio duration
    /// 6. Save to permanent storage
    /// 7. Create processing transcript in History
    /// 8. Transcribe via orchestrator (same as live recording)
    /// 9. Update transcript with results
    /// 10. Smart paste/copy to clipboard
    /// 11. Show success and cleanup
    ///
    /// ERROR HANDLING:
    /// - File not found: Error toast
    /// - File too large: Error toast with max size
    /// - Conversion failed: Error toast with reason
    /// - Transcription failed: Create failed transcript in History for retry
    /// </summary>
    public async Task TranscribeFileAsync(string filePath)
    {
        if (!CanStartFileTranscription())
        {
            return;
        }

        if (SelectedMode == null)
        {
            LoggingService.Warn($"TranscribeFileAsync: No mode selected - ModeCount={Modes.Count}");
            ShowErrorToastRequested?.Invoke(this, new ErrorToastEventArgs(
                Loc.S("errors.noModeSelected"),
                showSettingsButton: false));
            return;
        }

        await TranscribeFileAsync(filePath, SelectedMode);
    }

    private async Task TranscribeFileAsync(string filePath, Mode mode)
    {
        if (!CanStartFileTranscription())
        {
            return;
        }

        // STEP 1: Validate file
        if (!File.Exists(filePath))
        {
            ShowErrorToastRequested?.Invoke(this, new ErrorToastEventArgs(
                Loc.S("errors.fileNotFound"), showSettingsButton: false));
            return;
        }

        var requiresMuseNormalization = mode.ProviderType?.Equals("cloud", StringComparison.OrdinalIgnoreCase) == true
            && ((string.Equals(mode.CloudProvider, "hyperwhisper", StringComparison.OrdinalIgnoreCase)
                 && string.Equals(mode.CloudAccuracyTier, "metaMuse", StringComparison.OrdinalIgnoreCase))
                || string.Equals(mode.CloudProvider, "meta", StringComparison.OrdinalIgnoreCase));

        // STEP 2: Check file size per provider. Muse validates the normalized
        // artifact, because a larger compressed source can become a <=32 MB WAV.
        var fileInfo = new FileInfo(filePath);
        var maxSize = GetMaxFileSizeForProvider(mode);
        if (!requiresMuseNormalization && fileInfo.Length > maxSize)
        {
            ShowErrorToastRequested?.Invoke(this, new ErrorToastEventArgs(
                Loc.S("errors.fileTooLarge", ByteSizeFormatter.FormatDecimal(maxSize)),
                showSettingsButton: false));
            return;
        }

        if (requiresMuseNormalization)
        {
            if (ExceedsMuseSourceLimit(fileInfo.Length))
            {
                ShowErrorToastRequested?.Invoke(this, new ErrorToastEventArgs(
                    Loc.S("errors.fileTooLarge", ByteSizeFormatter.FormatDecimal(MetaMuseAudioContract.MaximumSourceBytes)),
                    showSettingsButton: false));
                return;
            }
            var sourceDuration = FileTranscriptionService.GetAudioDuration(filePath);
            if (sourceDuration.IsSuccess && sourceDuration.Value > 10 * 60)
            {
                ShowErrorToastRequested?.Invoke(this, new ErrorToastEventArgs(
                    "Meta Muse supports audio up to 10 minutes.", showSettingsButton: false));
                return;
            }
        }

        if (!await EnsureLocalProviderReadyForFileAsync(mode))
        {
            return;
        }

        // FILE TRANSCRIPTION PROGRESS TRACKING
        var fileName = Path.GetFileName(filePath);
        bool isCancelled = false;
        var transcriptionCts = new CancellationTokenSource();
        _activeTranscriptionCts = transcriptionCts;
        var transcriptDeleted = false;

        // STEP 3: Show progress window
        IsTranscribing = true;
        _fileTranscriptionActive = true;
        ShowFileProgressRequested?.Invoke(this, new FileTranscriptionProgressEventArgs(
            fileName,
            onCancel: () =>
            {
                isCancelled = true;
                CancelActiveTranscription();
            }
        ));

        Transcript? transcript = null;
        string? permanentPath = null;
        string? convertedTempPath = null;
        bool ownsPathForTranscription = false;
        double duration = 0;
        // Set only while the M4A compression is awaited, so a Cancel in that window can
        // hand the still-running encode to FinishRecordingCompressionAsync (#1499).
        Task<string?>? compression = null;

        try
        {
            LoggingService.Info($"TranscribeFileAsync: Starting file transcription - {filePath}");

            // STEP 4: Preparing stage (0-15%) - Convert format if needed
            UpdateFileProgressRequested?.Invoke(this, 0.05f);
            string pathForTranscription;

            if (mode.ProviderType == "cloud" && !requiresMuseNormalization)
            {
                // Cloud providers accept mp3/m4a/wav natively — send original file as-is
                pathForTranscription = filePath;
                LoggingService.Info($"TranscribeFileAsync: Cloud mode - skipping WAV conversion, using original file");
            }
            else
            {
                // Local WhisperNet requires 16kHz mono WAV
                var convertResult = await FileTranscriptionService.ConvertToWhisperFormatAsync(
                    filePath,
                    transcriptionCts.Token);
                if (convertResult.IsFailure)
                {
                    throw new Exception(convertResult.Error);
                }
                pathForTranscription = convertResult.Value!;
                ownsPathForTranscription = !string.Equals(
                    Path.GetFullPath(pathForTranscription),
                    Path.GetFullPath(filePath),
                    StringComparison.OrdinalIgnoreCase);
                convertedTempPath = ownsPathForTranscription ? pathForTranscription : null;
                LoggingService.Info($"TranscribeFileAsync: Converted to canonical WAV: {pathForTranscription}");
            }
            transcriptionCts.Token.ThrowIfCancellationRequested();

            if (requiresMuseNormalization && new FileInfo(pathForTranscription).Length > 32L * 1024 * 1024)
            {
                throw new InvalidOperationException("The normalized audio exceeds Meta Muse's 32 MB upload limit.");
            }

            // STEP 5: Get duration
            UpdateFileProgressRequested?.Invoke(this, 0.10f);
            var durationResult = FileTranscriptionService.GetAudioDuration(pathForTranscription);
            if (durationResult.IsFailure)
            {
                throw new Exception(durationResult.Error);
            }
            transcriptionCts.Token.ThrowIfCancellationRequested();
            duration = durationResult.Value;
            if (requiresMuseNormalization && duration > 10 * 60)
            {
                throw new InvalidOperationException("Meta Muse supports audio up to 10 minutes.");
            }

            // STEP 6: Save file to permanent location
            UpdateFileProgressRequested?.Invoke(this, 0.15f);
            permanentPath = HistoryService.Instance.SaveAudioFile(pathForTranscription, ownsPathForTranscription);
            LoggingService.Info($"TranscribeFileAsync: Audio saved ({permanentPath}, {fileInfo.Length:N0} bytes, {duration:F2}s)");

            // STEP 7: Create processing transcript
            transcript = HistoryService.Instance.CreateProcessingTranscript(
                duration, mode.Name, permanentPath);

            // STEP 8: Transcribing stage (15-85%) - Start slow animation to 80%
            // (will be cut short when transcription completes)
            UpdateFileProgressRequested?.Invoke(this, 0.80f);

            var vocabulary = _vocabularyService.GetVocabularyWords(100);
            var result = await _transcriptionOrchestrator.TranscribeAsync(
                permanentPath, mode, vocabulary,
                localTranscriptionProvider: GetLocalProvider(mode),
                cancellationToken: transcriptionCts.Token,
                // Already probed above (STEP 5) via NAudio for the same audio
                // content (permanentPath is a byte-identical copy of
                // pathForTranscription) — avoids AssemblyAIService re-reading
                // the file a second time just for its sync-eligibility gate.
                knownDurationSeconds: duration);
            transcriptionCts.Token.ThrowIfCancellationRequested();

            // STEP 9: Finishing stage (85-100%) - Update transcript with results
            UpdateFileProgressRequested?.Invoke(this, 0.85f);
            transcript.Text = result.FinalText;
            transcript.TranscribedText = result.RawText;
            transcript.PostProcessedText = result.PostProcessedText;
            transcript.Status = TranscriptStatus.Completed;
            transcript.TranscriptionProvider = result.TranscriptionProvider;
            transcript.PostProcessingProvider = result.PostProcessingProvider;

            // STORAGE: Optionally compress to M4A for space savings (local mode saves WAV).
            // The encode runs on the thread pool and this awaits it, so the window, the
            // tray, the hotkeys and the progress window's Cancel stay live (#1499). The
            // code after the await is back on the dispatcher.
            if (ShouldConvertImportedAudioToM4A(
                    _storageService.StoreAsM4A, pathForTranscription, permanentPath))
            {
                compression = StartRecordingCompression(_storageService.TryConvertWavToM4A, permanentPath);
                var compressedPath = await compression.WaitAsync(transcriptionCts.Token);
                compression = null; // Recorded below; the cancel path must not finish it again.
                if (!string.IsNullOrEmpty(compressedPath))
                {
                    if (HistoryService.Instance.GetTranscript(transcript.Id) == null)
                    {
                        // The user deleted the row while it compressed. Its delete could
                        // not remove the WAV (the encoder held it open) and never knew
                        // about the M4A.
                        LoggingService.Info($"TranscribeFileAsync: Transcript {transcript.Id} was removed while its audio compressed; deleting the M4A");
                        HistoryService.Instance.DeleteAudioFile(compressedPath);
                    }
                    else
                    {
                        transcript.AudioFilePath = compressedPath;
                    }
                }
            }

            HistoryService.Instance.UpdateTranscript(transcript);
            LoggingService.Info($"TranscribeFileAsync: Transcription complete - {result.FinalText.Length} chars");

            // STEP 10: Navigate to History to show result
            UpdateFileProgressRequested?.Invoke(this, 0.95f);
            CurrentPage = NavigationPage.History;

            // STEP 11: Complete and cleanup
            UpdateFileProgressRequested?.Invoke(this, 1.0f);
            await Task.Delay(500); // Brief pause to show 100%
            HideFileProgressRequested?.Invoke(this, EventArgs.Empty);

            // Clean up temp file if conversion created one (local mode only)
            if (convertedTempPath != null && convertedTempPath != permanentPath && File.Exists(convertedTempPath))
            {
                try
                {
                    File.Delete(convertedTempPath);
                    LoggingService.Debug($"TranscribeFileAsync: Deleted temp file - {convertedTempPath}");
                }
                catch (Exception ex)
                {
                    LoggingService.Warn($"TranscribeFileAsync: Failed to delete temp file: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) when (transcriptionCts.IsCancellationRequested || isCancelled)
        {
            LoggingService.Info("TranscribeFileAsync: File transcription cancelled by user");
            HideFileProgressRequested?.Invoke(this, EventArgs.Empty);
            // The audio came off disk and the microphone was never opened, so
            // "Recording cancelled" would describe something that did not happen (#506).
            StatusText = CancelledStatusText(cancelledFileTranscription: true);

            if (transcript != null)
            {
                // A delete SQLite refused (#974) keeps the row, as main did: the failed
                // Result leaves transcriptDeleted false, so the finally safety net marks the
                // row terminal. HistoryService already logged it.
                var deleteResult = HistoryService.Instance.DeleteTranscript(transcript.Id);
                transcriptDeleted = deleteResult.IsSuccess && deleteResult.Value;

                // Cancelled while the M4A compression ran (#1499). The encode cannot be
                // stopped part-way and holds the WAV open, so the delete above could not
                // remove it. Clean up once it ends: both files if the row went, else
                // point the kept row at the M4A. Both helpers never throw.
                if (compression != null && permanentPath != null)
                {
                    _ = transcriptDeleted
                        ? DeleteRecordingAfterCompressionAsync(
                            compression, permanentPath, HistoryService.Instance.DeleteAudioFile)
                        : FinishRecordingCompressionAsync(
                            compression, transcript.Id,
                            HistoryService.Instance.UpdateAudioFilePath,
                            HistoryService.Instance.DeleteAudioFile);
                }
            }
            else if (!string.IsNullOrEmpty(permanentPath))
            {
                HistoryService.Instance.DeleteAudioFile(permanentPath);
            }
        }
        catch (Exception ex)
        {
            LoggingService.Error($"TranscribeFileAsync failed: {ex.Message}", ex);
            HideFileProgressRequested?.Invoke(this, EventArgs.Empty);

            if (transcript != null)
            {
                if (ex is TranscriptionException txEx && txEx.Code == TranscriptionErrorCode.NoSpeechDetected && permanentPath != null)
                {
                    var failureWritten = MarkTranscriptAsNoSpeechFailure(transcript, txEx.ProviderName);

                    // Skip the diagnostic capture and toast if the guard no-op'd
                    // because the transcript was already persisted as Completed by
                    // another path.
                    if (failureWritten)
                    {
                        TranscriptionDiagnosticsService.CaptureNoSpeechDiagnostic(
                            transcriptId: transcript.Id,
                            audioPath: permanentPath,
                            fallbackDurationSeconds: duration,
                            mode: mode,
                            diagnosticStage: "file_transcription",
                            diagnosticSource: "provider_no_speech",
                            transcriptionProviderDisplayName: txEx.ProviderName,
                            providerDiagnostics: txEx.ProviderDiagnostics,
                            exception: txEx);

                        ShowErrorToastRequested?.Invoke(this, new ErrorToastEventArgs(
                            txEx.GetUserMessage(),
                            showSettingsButton: false));
                    }
                }
                else
                {
                    var failureWritten = MarkTranscriptAsGenericFailure(transcript, ex);
                    if (failureWritten)
                    {
                        ShowErrorToastRequested?.Invoke(this, new ErrorToastEventArgs(
                            Loc.S("errors.transcriptionFailed", ex.Message), showSettingsButton: false));
                    }
                }
            }
            else
            {
                ShowErrorToastRequested?.Invoke(this, new ErrorToastEventArgs(
                    ex is TranscriptionException txEx
                        ? txEx.GetUserMessage()
                        : Loc.S("errors.transcriptionFailed", ex.Message),
                    showSettingsButton: false));
            }
        }
        finally
        {
            // SAFETY NET: Ensure the transcript is never left stuck in Processing.
            // The isCancelled early-returns above bail out without writing a terminal
            // status, which would leave the History row spinning forever.
            if (!transcriptDeleted)
            {
                EnsureTranscriptTerminalStatus(transcript);
            }

            if (convertedTempPath != null && convertedTempPath != permanentPath && File.Exists(convertedTempPath))
            {
                try
                {
                    File.Delete(convertedTempPath);
                    LoggingService.Debug($"TranscribeFileAsync: Deleted temp file - {convertedTempPath}");
                }
                catch (Exception ex)
                {
                    LoggingService.Warn($"TranscribeFileAsync: Failed to delete temp file: {ex.Message}");
                }
            }

            IsTranscribing = false;
            _fileTranscriptionActive = false;
            if (ReferenceEquals(_activeTranscriptionCts, transcriptionCts))
            {
                _activeTranscriptionCts = null;
            }
            transcriptionCts.Dispose();
        }
    }

    internal static bool ShouldConvertImportedAudioToM4A(
        bool storeAsM4A, string pathForTranscription, string historyPath) =>
        storeAsM4A
        && string.Equals(
            Path.GetExtension(pathForTranscription), ".wav", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(
            Path.GetFullPath(pathForTranscription),
            Path.GetFullPath(historyPath),
            StringComparison.OrdinalIgnoreCase);

    internal static bool ExceedsMuseSourceLimit(long sourceBytes) =>
        sourceBytes > MetaMuseAudioContract.MaximumSourceBytes;

    private bool CanStartFileTranscription()
    {
        if (IsRecording || IsTranscribing || _activeTranscriptionCts != null)
        {
            LoggingService.Warn("File transcription requested while recording or transcribing; ignoring request");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Provider file size limits (matches macOS FileTranscriptionFlow.swift).
    ///
    /// LIMITS:
    /// - Local: No limit (only constrained by available memory/disk)
    /// - Cloud: Uses the selected CloudProvider's declared max size
    /// - Missing cloud provider: falls back to a conservative 25 MB
    /// </summary>
    private long GetMaxFileSizeForProvider(Mode mode)
    {
        if (mode.ProviderType?.Equals("local", StringComparison.OrdinalIgnoreCase) == true)
        {
            return long.MaxValue;
        }

        if (mode.ProviderType?.Equals("cloud", StringComparison.OrdinalIgnoreCase) == true)
        {
            var provider = CloudTranscriptionProviderExtensions.FromIdentifier(mode.CloudProvider);
            return provider != CloudTranscriptionProvider.None
                ? provider.GetMaxFileSizeBytes()
                : 25L * 1024 * 1024;
        }

        return long.MaxValue;
    }

    private async Task<bool> EnsureLocalProviderReadyForFileAsync(Mode mode)
    {
        if (mode.ProviderType?.Equals("cloud", StringComparison.OrdinalIgnoreCase) == true)
        {
            return true;
        }

        if (mode.LocalEngine == "parakeet")
        {
            var model = ParakeetModelInfo.AllModels.FirstOrDefault(m => m.Id == mode.LocalParakeetModel);
            if (model == null || !_parakeetModelService.IsModelDownloaded(model))
            {
                var modelName = mode.LocalParakeetModel ?? "Unknown";
                ShowErrorToastRequested?.Invoke(this, new ErrorToastEventArgs(
                    Loc.S("errors.modelNotDownloaded", modelName),
                    showSettingsButton: false));
                return false;
            }

            string? language = mode.Language == "auto" ? null : mode.Language;

            // A warm daemon can be reused for file transcription unless
            // NeedsReload's per-engine rules say the switch requires a respawn.
            if (!_parakeetTranscriptionService.NeedsReload(model.Id, mode.Language))
            {
                return true;
            }

            // #1534: serialize with the other model loads. The Whisper unload can
            // wait out a running job with the UI thread free, so without the lock a
            // mode switch or dictation start could start a second daemon meanwhile.
            await _modelLoadLock.WaitAsync();
            try
            {
                // Re-check after acquiring the lock: a load queued ahead of this
                // one may already have started the daemon this file needs. No
                // SelectedMode re-check here: the file job asked for THIS mode.
                if (!_parakeetTranscriptionService.NeedsReload(model.Id, mode.Language))
                {
                    return true;
                }

                IsModelLoading = true;
                StatusText = Loc.S("status.model.parakeet.loading", model.DisplayName);
                await UnloadWhisperForParakeetIfLowMemoryAsync("EnsureLocalProviderReadyForFileAsync");
                await _parakeetTranscriptionService.InitializeAsync(
                    _parakeetModelService.GetModelDirectory(model),
                    language);
                IsModelLoaded = true;
                ModelStatus = Loc.S("status.model.parakeet.ready", model.DisplayName, _parakeetTranscriptionService.ActiveProvider ?? "CPU");
                return true;
            }
            catch (Exception ex)
            {
                ModelStatus = Loc.S("status.model.loadFailed");
                StatusText = Loc.S("status.failed", ex.Message);
                LoggingService.Error($"EnsureLocalProviderReadyForFileAsync: Parakeet model load failed - {ex.Message}", ex);
                ShowErrorToastRequested?.Invoke(this, new ErrorToastEventArgs(
                    Loc.S("errors.modelLoadFailed"),
                    showSettingsButton: false));
                return false;
            }
            finally
            {
                IsModelLoading = false;
                _modelLoadLock.Release();
            }
        }

        if (!PlatformHelper.SupportsWhisperTranscription)
        {
            ShowErrorToastRequested?.Invoke(this, new ErrorToastEventArgs(
                Loc.S("errors.modelLoadFailed"),
                showSettingsButton: false));
            return false;
        }

        var whisperModel = WhisperModelInfo.AllModels.FirstOrDefault(m => m.Type == mode.ModelType);
        if (whisperModel == null || !_modelService.IsModelDownloaded(whisperModel))
        {
            var modelName = mode.ModelType ?? "Unknown";
            ShowErrorToastRequested?.Invoke(this, new ErrorToastEventArgs(
                Loc.S("errors.modelNotDownloaded", modelName),
                showSettingsButton: false));
            return false;
        }

        var modelPath = _modelService.GetModelPath(whisperModel);
        if (_transcriptionService.IsInitialized && _transcriptionService.LoadedModelPath == modelPath)
        {
            return true;
        }

        await _modelLoadLock.WaitAsync();
        try
        {
            if (_transcriptionService.IsInitialized && _transcriptionService.LoadedModelPath == modelPath)
            {
                return true;
            }

            // Inside the lock (#1534), as in LoadWhisperModelAsync: dispose the
            // daemon a Parakeet load queued ahead of this one may have started.
            if (_parakeetTranscriptionService.IsInitialized)
            {
                LoggingService.Info("EnsureLocalProviderReadyForFileAsync: Disposing Parakeet daemon before Whisper file transcription");
                _parakeetTranscriptionService.DisposeModel();
            }

            IsModelLoading = true;
            StatusText = Loc.S("status.model.loading", whisperModel.DisplayName);
            await _transcriptionService.InitializeAsync(modelPath, p => { }, CancellationToken.None);
            IsModelLoaded = true;
            ModelStatus = Loc.S("status.model.ready", whisperModel.DisplayName);
            return true;
        }
        catch (Exception ex)
        {
            ModelStatus = Loc.S("status.model.loadFailed");
            StatusText = Loc.S("status.failed", ex.Message);
            LoggingService.Error($"EnsureLocalProviderReadyForFileAsync: Whisper model load failed - {ex.Message}", ex);
            ShowErrorToastRequested?.Invoke(this, new ErrorToastEventArgs(
                Loc.S("errors.modelLoadFailed"),
                showSettingsButton: false));
            return false;
        }
        finally
        {
            IsModelLoading = false;
            _modelLoadLock.Release();
        }
    }

}
