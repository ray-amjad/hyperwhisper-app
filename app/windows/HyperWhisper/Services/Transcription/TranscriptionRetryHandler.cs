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
// - Uses the shared TranscriptionRuntime orchestrator and local providers;
//   tests pass the transcription and History save as delegates (#1037)

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

    /// <summary>
    /// The orchestrator's <c>TranscribeAsync</c>, as the retry calls it: audio path,
    /// mode, vocabulary, local provider, application context, token.
    /// </summary>
    internal delegate Task<TranscriptionResult> TranscribeAudio(
        string audioPath,
        Mode mode,
        IReadOnlyList<string>? vocabulary,
        ITranscriptionProvider? localTranscriptionProvider,
        ApplicationContext? applicationContext,
        CancellationToken cancellationToken);

    private readonly TranscribeAudio _transcribe;
    private readonly TranscriptionService _localTranscriptionService;
    private readonly WhisperModelService _modelService;
    private readonly ParakeetTranscriptionService _parakeetTranscriptionService;
    private readonly ParakeetModelService _parakeetModelService;
    private readonly Action<Transcript> _saveTranscript;
    private bool _disposed;

    /// <summary>PROBE ONLY (#1037 mutation matrix): which mutant is live; 0 = none.</summary>
    internal static int Mutant;

    // =========================================================================
    // CONSTRUCTOR
    // =========================================================================

    public TranscriptionRetryHandler()
        : this(TranscribeWith(TranscriptionRuntime.Orchestrator), SaveTo(HistoryService.Instance))
    {
    }

    /// <summary>
    /// Test seam (#1037): the two I/O edges of a retry, the transcription and the
    /// History save, as plain delegates. The local providers stay the shared ones.
    /// </summary>
    internal TranscriptionRetryHandler(TranscribeAudio transcribe, Action<Transcript> saveTranscript)
    {
        _transcribe = transcribe ?? throw new ArgumentNullException(nameof(transcribe));
        _saveTranscript = saveTranscript ?? throw new ArgumentNullException(nameof(saveTranscript));

        // The local transcription primitives come from TranscriptionRuntime
        // so the History retry path shares the same loaded model the GUI
        // and Local API see. Otherwise a retry on an <32GB box would force
        // a third Whisper instance + native load.
        _localTranscriptionService = TranscriptionRuntime.LocalProvider;
        _modelService = new WhisperModelService();
        _parakeetTranscriptionService = TranscriptionRuntime.ParakeetProvider;
        _parakeetModelService = new ParakeetModelService();

        LoggingService.Info("TranscriptionRetryHandler: Initialized");
    }

    private static TranscribeAudio TranscribeWith(TranscriptionOrchestrator orchestrator) =>
        (audioPath, mode, vocabulary, localProvider, applicationContext, cancellationToken) =>
            orchestrator.TranscribeAsync(
                audioPath,
                mode,
                vocabulary: vocabulary,
                localTranscriptionProvider: localProvider,
                applicationContext: applicationContext,
                cancellationToken: cancellationToken);

    private static Action<Transcript> SaveTo(HistoryService historyService) =>
        transcript => historyService.UpdateTranscript(transcript);

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
        if (Mutant != 8 && transcript.Status != TranscriptStatus.Failed) return false;
        if (string.IsNullOrEmpty(transcript.AudioFilePath)) return false;
        if (Mutant != 7 && !File.Exists(transcript.AudioFilePath)) return false;
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
            // Ensure local model is loaded for local modes. A Parakeet lease keeps the
            // model loaded until this retry's transcription has ended (#1608).
            using var parakeetModelLease = mode.ProviderType != "cloud"
                ? await EnsureLocalModelLoadedAsync(mode, cancellationToken)
                : null;

            // Select correct provider based on engine
            ITranscriptionProvider? localProvider = null;
            if (mode.ProviderType != "cloud")
            {
                localProvider = mode.LocalEngine == "parakeet"
                    ? _parakeetTranscriptionService
                    : _localTranscriptionService;
            }

            // An on-device Whisper retry gets the vocabulary a recording gets, so it
            // keeps its prompt on a retry (#1564). A cloud retry and a Parakeet-family
            // retry (its vocabulary pass, STEP 2b) still get none, as before.
            var vocabulary = localProvider is not null and not ILocalVocabularyCorrection
                ? VocabularyService.Instance.GetVocabularyWords(100)
                : null;

            var result = await _transcribe(
                transcript.AudioFilePath!,
                mode,
                vocabulary,
                localProvider,
                null,
                cancellationToken);

            // Update transcript entity with success
            UpdateTranscriptSuccess(transcript, mode, result);
            if (Mutant == 5) transcript.IsRetrying = false;

            LoggingService.Info($"TranscriptionRetryHandler: Retry successful for transcript {transcript.Id}");

            return result;
        }
        catch (Exception ex)
        {
            // Update transcript entity with failure
            UpdateTranscriptFailure(transcript, ex);

            LoggingService.Error($"TranscriptionRetryHandler: Retry failed for transcript {transcript.Id}: {ex.Message}");
            if (Mutant == 6) return null!;
            throw;
        }
        finally
        {
            if (Mutant != 5) transcript.IsRetrying = false;
        }
    }

    // =========================================================================
    // PRIVATE HELPERS
    // =========================================================================

    /// <returns>The Parakeet model lease (#1608), or null for Whisper.</returns>
    private async Task<IDisposable?> EnsureLocalModelLoadedAsync(Mode mode, CancellationToken cancellationToken)
    {
        if (mode.LocalEngine == "parakeet")
        {
            return await EnsureParakeetModelLoadedAsync(mode, cancellationToken);
        }

        await EnsureWhisperModelLoadedAsync(mode, cancellationToken);
        return null;
    }

    private async Task EnsureWhisperModelLoadedAsync(Mode mode, CancellationToken cancellationToken)
    {
        var modelInfo = WhisperModelInfo.AllModels.FirstOrDefault(m => m.Type == mode.ModelType);
        if (modelInfo == null)
        {
            throw new InvalidOperationException($"Model type '{mode.ModelType}' not found");
        }

        var modelPath = _modelService.GetModelPath(modelInfo);

        // Skip if already loaded with same model, unless an unload is about to
        // dispose it (#1609): then queue behind the unload and load it again.
        if (_localTranscriptionService.IsLoadedAndStaying(modelPath))
        {
            return;
        }

        LoggingService.Info($"TranscriptionRetryHandler: Loading Whisper model {modelInfo.DisplayName}");
        await _localTranscriptionService.InitializeAsync(modelPath, progress => { }, cancellationToken);
    }

    private async Task<IDisposable> EnsureParakeetModelLoadedAsync(Mode mode, CancellationToken cancellationToken)
    {
        var modelInfo = ParakeetModelInfo.AllModels.FirstOrDefault(m => m.Id == mode.LocalParakeetModel);
        if (modelInfo == null)
        {
            throw new InvalidOperationException($"Parakeet model '{mode.LocalParakeetModel}' not found");
        }

        var modelDir = _parakeetModelService.GetModelDirectory(modelInfo);

        string? language = mode.Language == "auto" ? null : mode.Language;

        if (_parakeetTranscriptionService.NeedsReload(modelInfo.Id, mode.Language))
        {
            LoggingService.Info($"TranscriptionRetryHandler: Loading Parakeet model {modelInfo.DisplayName}");
        }

        // A retry must not end another job on the shared daemon (#1608): wait until it is
        // idle (a warm daemon that fits returns at once), and hold the model until the
        // retry has transcribed.
        return await _parakeetTranscriptionService.ReloadWhenIdleAsync(modelInfo.Id, modelDir, language, cancellationToken);
    }

    private void UpdateTranscriptSuccess(TranscriptViewModel transcript, Mode mode, TranscriptionResult result)
    {
        var entity = transcript.ToEntity();
        entity.Text = result.FinalText;
        entity.TranscribedText = result.RawText;
        entity.PostProcessedText = result.PostProcessedText;
        entity.Status = TranscriptStatus.Completed;
        if (Mutant != 4) entity.FailedReason = null;
        entity.TranscriptionProvider = result.TranscriptionProvider;
        entity.PostProcessingProvider = result.PostProcessingProvider;
        if (Mutant != 1) entity.RetryCount++;
        entity.LastRetryDate = DateTime.UtcNow;
        entity.Mode = mode.Name;

        _saveTranscript(entity);

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
        var failureMessage = Mutant != 3 && ex is TranscriptionException txEx
            ? txEx.GetUserMessage()
            : ex.Message;

        var entity = transcript.ToEntity();
        entity.Status = TranscriptStatus.Failed;
        entity.FailedReason = failureMessage;
        entity.Text = $"Transcription failed: {failureMessage}";
        if (Mutant != 2) entity.RetryCount++;
        entity.LastRetryDate = DateTime.UtcNow;

        _saveTranscript(entity);

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
