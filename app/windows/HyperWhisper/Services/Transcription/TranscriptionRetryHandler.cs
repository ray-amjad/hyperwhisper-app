// TRANSCRIPTION RETRY HANDLER
// Handles retry-specific concerns for failed transcriptions in History view.
//
// RESPONSIBILITIES:
// - Validate retry eligibility
// - Manage retry status and count
// - Update transcript entity with results
// - Delegate actual transcription to orchestrator
//
// DESIGN:
// - Receives TranscriptViewModel for UI state updates
// - Returns TranscriptionResult for flexibility
// - Handles both cloud and local retries
// - Owns its own TranscriptionOrchestrator and TranscriptionService

using System.IO;
using HyperWhisper.Data.Entities;
using HyperWhisper.Models;
using HyperWhisper.ViewModels;

namespace HyperWhisper.Services.Transcription;

/// <summary>
/// Handler for retry transcription operations from the History view.
/// Manages the full retry lifecycle: validation, transcription, and entity updates.
/// </summary>
public class TranscriptionRetryHandler : IDisposable
{
    // =========================================================================
    // DEPENDENCIES
    // =========================================================================

    private readonly TranscriptionOrchestrator _orchestrator;
    private readonly TranscriptionService _localTranscriptionService;
    private readonly WhisperModelService _modelService;
    private readonly ParakeetTranscriptionService _parakeetTranscriptionService;
    private readonly ParakeetModelService _parakeetModelService;
    private readonly HistoryService _historyService;
    private bool _disposed;

    // =========================================================================
    // CONSTRUCTOR
    // =========================================================================

    public TranscriptionRetryHandler()
    {
        // All three transcription primitives come from TranscriptionRuntime
        // so the History retry path shares the same loaded model the GUI
        // and Local API see. Otherwise a retry on an <32GB box would force
        // a third Whisper instance + native load.
        _orchestrator = TranscriptionRuntime.Orchestrator;
        _localTranscriptionService = TranscriptionRuntime.LocalProvider;
        _modelService = new WhisperModelService();
        _parakeetTranscriptionService = TranscriptionRuntime.ParakeetProvider;
        _parakeetModelService = new ParakeetModelService();
        _historyService = HistoryService.Instance;

        LoggingService.Info("TranscriptionRetryHandler: Initialized");
    }

    // =========================================================================
    // PUBLIC API
    // =========================================================================

    /// <summary>
    /// Checks if a transcript can be retried.
    /// Requirements: failed status, audio file exists.
    /// </summary>
    public bool CanRetry(TranscriptViewModel? transcript)
    {
        if (transcript == null) return false;
        if (transcript.Status != TranscriptStatus.Failed) return false;
        if (string.IsNullOrEmpty(transcript.AudioFilePath)) return false;
        if (!File.Exists(transcript.AudioFilePath)) return false;
        return true;
    }

    /// <summary>
    /// Retries transcription for a failed transcript.
    /// Updates the transcript entity and view model with results.
    /// </summary>
    /// <param name="transcript">The transcript view model to retry.</param>
    /// <param name="mode">Mode to use for retry (may differ from original).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>TranscriptionResult if successful.</returns>
    /// <exception cref="InvalidOperationException">If transcript cannot be retried.</exception>
    public async Task<TranscriptionResult> RetryTranscriptionAsync(
        TranscriptViewModel transcript,
        Mode mode,
        CancellationToken cancellationToken = default)
    {
        // Validation
        if (!CanRetry(transcript))
        {
            throw new InvalidOperationException("Transcript cannot be retried");
        }

        if (mode == null)
        {
            throw new ArgumentNullException(nameof(mode));
        }

        LoggingService.Info($"TranscriptionRetryHandler: Retrying transcript {transcript.Id} with mode '{mode.Name}'");

        // Update UI state
        transcript.IsRetrying = true;

        try
        {
            // Ensure local model is loaded for local modes
            if (mode.ProviderType != "cloud")
            {
                await EnsureLocalModelLoadedAsync(mode, cancellationToken);
            }

            // Select correct provider based on engine
            ITranscriptionProvider? localProvider = null;
            if (mode.ProviderType != "cloud")
            {
                localProvider = mode.LocalEngine == "parakeet"
                    ? _parakeetTranscriptionService
                    : _localTranscriptionService;
            }

            var result = await _orchestrator.TranscribeAsync(
                transcript.AudioFilePath!,
                mode,
                vocabulary: null,
                localTranscriptionProvider: localProvider,
                applicationContext: null,
                cancellationToken: cancellationToken);

            // Update transcript entity with success
            UpdateTranscriptSuccess(transcript, mode, result);

            LoggingService.Info($"TranscriptionRetryHandler: Retry successful for transcript {transcript.Id}");

            return result;
        }
        catch (Exception ex)
        {
            // Update transcript entity with failure
            UpdateTranscriptFailure(transcript, ex);

            LoggingService.Error($"TranscriptionRetryHandler: Retry failed for transcript {transcript.Id}: {ex.Message}");
            throw;
        }
        finally
        {
            transcript.IsRetrying = false;
        }
    }

    // =========================================================================
    // PRIVATE HELPERS
    // =========================================================================

    private async Task EnsureLocalModelLoadedAsync(Mode mode, CancellationToken cancellationToken)
    {
        if (mode.LocalEngine == "parakeet")
        {
            await EnsureParakeetModelLoadedAsync(mode, cancellationToken);
        }
        else
        {
            await EnsureWhisperModelLoadedAsync(mode, cancellationToken);
        }
    }

    private async Task EnsureWhisperModelLoadedAsync(Mode mode, CancellationToken cancellationToken)
    {
        var modelInfo = WhisperModelInfo.AllModels.FirstOrDefault(m => m.Type == mode.ModelType);
        if (modelInfo == null)
        {
            throw new InvalidOperationException($"Model type '{mode.ModelType}' not found");
        }

        var modelPath = _modelService.GetModelPath(modelInfo);

        // Skip if already loaded with same model
        if (_localTranscriptionService.IsInitialized &&
            _localTranscriptionService.LoadedModelPath == modelPath)
        {
            return;
        }

        LoggingService.Info($"TranscriptionRetryHandler: Loading Whisper model {modelInfo.DisplayName}");
        await _localTranscriptionService.InitializeAsync(modelPath, progress => { }, cancellationToken);
    }

    private async Task EnsureParakeetModelLoadedAsync(Mode mode, CancellationToken cancellationToken)
    {
        var modelInfo = ParakeetModelInfo.AllModels.FirstOrDefault(m => m.Id == mode.LocalParakeetModel);
        if (modelInfo == null)
        {
            throw new InvalidOperationException($"Parakeet model '{mode.LocalParakeetModel}' not found");
        }

        var modelDir = _parakeetModelService.GetModelDirectory(modelInfo);

        string? language = mode.Language == "auto" ? null : mode.Language;
        var effectiveLanguage = language ?? "auto";

        // Skip if already loaded with same model and language hint
        if (_parakeetTranscriptionService.IsInitialized &&
            _parakeetTranscriptionService.LoadedModelId == modelInfo.Id &&
            string.Equals(_parakeetTranscriptionService.LoadedLanguage, effectiveLanguage, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        LoggingService.Info($"TranscriptionRetryHandler: Loading Parakeet model {modelInfo.DisplayName}");
        await _parakeetTranscriptionService.InitializeAsync(modelDir, language);
    }

    private void UpdateTranscriptSuccess(TranscriptViewModel transcript, Mode mode, TranscriptionResult result)
    {
        var entity = transcript.ToEntity();
        entity.Text = result.FinalText;
        entity.TranscribedText = result.RawText;
        entity.PostProcessedText = result.PostProcessedText;
        entity.Status = TranscriptStatus.Completed;
        entity.FailedReason = null;
        entity.TranscriptionProvider = result.TranscriptionProvider;
        entity.PostProcessingProvider = result.PostProcessingProvider;
        entity.RetryCount++;
        entity.LastRetryDate = DateTime.UtcNow;
        entity.Mode = mode.Name;

        _historyService.UpdateTranscript(entity);

        // Update view model to reflect changes immediately
        transcript.Text = result.FinalText;
        transcript.TranscribedText = result.RawText;
        transcript.PostProcessedText = result.PostProcessedText;
        transcript.Status = TranscriptStatus.Completed;
        transcript.FailedReason = null;
        transcript.TranscriptionProvider = result.TranscriptionProvider;
        transcript.PostProcessingProvider = result.PostProcessingProvider;
        transcript.RetryCount++;
        transcript.LastRetryDate = DateTime.UtcNow;
        transcript.Mode = mode.Name;
    }

    private void UpdateTranscriptFailure(TranscriptViewModel transcript, Exception ex)
    {
        var failureMessage = ex is TranscriptionException txEx
            ? txEx.GetUserMessage()
            : ex.Message;

        var entity = transcript.ToEntity();
        entity.Status = TranscriptStatus.Failed;
        entity.FailedReason = failureMessage;
        entity.Text = $"Transcription failed: {failureMessage}";
        entity.RetryCount++;
        entity.LastRetryDate = DateTime.UtcNow;

        _historyService.UpdateTranscript(entity);

        // Update view model to reflect changes immediately
        transcript.Status = TranscriptStatus.Failed;
        transcript.FailedReason = failureMessage;
        transcript.Text = $"Transcription failed: {failureMessage}";
        transcript.RetryCount++;
        transcript.LastRetryDate = DateTime.UtcNow;
    }

    // =========================================================================
    // DISPOSAL
    // =========================================================================

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Orchestrator + local providers come from TranscriptionRuntime —
        // process-scoped and shared with the GUI and Local API. Don't
        // dispose them here.

        LoggingService.Info("TranscriptionRetryHandler: Disposed");
        GC.SuppressFinalize(this);
    }
}
