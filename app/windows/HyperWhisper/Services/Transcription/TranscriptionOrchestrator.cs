// TRANSCRIPTION ORCHESTRATOR
// Central routing hub for all transcription operations.
// Replaces duplicated logic in MainViewModel and HistoryViewModel.
//
// RESPONSIBILITIES:
// - Route to correct provider (cloud vs local) based on mode
// - Handle post-processing if enabled
// - Apply vocabulary replacements
// - Return complete TranscriptionResult for history
//
// DESIGN:
// - Does NOT manage TranscriptionService lifecycle (model loading)
// - Caller must ensure local model is loaded before local transcription
// - Post-processing failures are graceful (returns raw text with warning)

using HyperWhisper.Data.Entities;
using HyperWhisper.Models;
using HyperWhisper.Utilities;

namespace HyperWhisper.Services.Transcription;

/// <summary>
/// Central orchestrator for all transcription operations.
/// Routes to cloud or local providers and handles post-processing.
/// </summary>
public class TranscriptionOrchestrator : IDisposable
{
    // =========================================================================
    // DEPENDENCIES
    // =========================================================================

    private readonly TranscriptionProviderFactory _providerFactory;
    private readonly PostProcessingService _postProcessingService;
    private readonly VocabularyProcessor _vocabularyProcessor;
    private bool _disposed;

    // Foreground keepalive — periodic warmup while the app is the active window.
    // `SocketsHttpHandler` lets pooled HTTP/2 connections idle out after a few
    // minutes; pinging /warmup every 45s keeps the pool warm so a hotkey press
    // after a long idle window still reuses the connection instead of paying
    // TCP+TLS handshake again.
    private System.Timers.Timer? _keepaliveTimer;
    private Func<Mode?>? _keepaliveModeGetter;
    // `System.Timers.Timer.Stop()` doesn't cancel an already-queued Elapsed
    // callback on the ThreadPool, so a stale tick can still fire after
    // Deactivated / Dispose. Check this flag inside the tick handler.
    private volatile bool _keepaliveStopped;
    private static readonly TimeSpan KeepaliveInterval = TimeSpan.FromSeconds(45);

    /// <summary>
    /// Event raised when post-processing fails but transcription succeeded.
    /// Allows UI to show warning to user. Args are
    /// <see cref="OrchestratorPostProcessingWarningEventArgs"/> when emitted
    /// during <see cref="TranscribeAsync"/> so subscribers can branch on
    /// <see cref="TranscriptionCallSite"/>.
    /// </summary>
    public event EventHandler<ErrorToastEventArgs>? PostProcessingWarning;

    /// <summary>
    /// Tracks the call site of the currently-running TranscribeAsync on this
    /// async flow so the bridged PostProcessingWarning can tag itself before
    /// raising to subscribers. AsyncLocal so concurrent API+GUI calls don't
    /// step on each other.
    /// </summary>
    private static readonly System.Threading.AsyncLocal<TranscriptionCallSite> _currentCallSite = new();

    // =========================================================================
    // CONSTRUCTOR
    // =========================================================================

    public TranscriptionOrchestrator()
    {
        _providerFactory = new TranscriptionProviderFactory();
        _postProcessingService = new PostProcessingService();
        _vocabularyProcessor = new VocabularyProcessor();

        // Forward post-processing warnings to callers
        _postProcessingService.WarningOccurred += OnPostProcessingWarning;

        LoggingService.Info("TranscriptionOrchestrator: Initialized");
    }

    private void OnPostProcessingWarning(object? sender, ErrorToastEventArgs e)
    {
        // Tag the warning with the current call site so GUI subscribers can
        // suppress the toast for API-driven calls. If the warning came from
        // outside a TranscribeAsync flow (PostProcessingService called
        // directly), AsyncLocal returns the default (Gui) — safe baseline.
        var tagged = new OrchestratorPostProcessingWarningEventArgs(e, _currentCallSite.Value);
        PostProcessingWarning?.Invoke(this, tagged);
    }

    /// <summary>
    /// Pre-warms the HyperWhisper Cloud connection if the given mode resolves to cloud.
    /// Fire-and-forget — safe to call from any hotkey-down path.
    /// </summary>
    public void PrewarmCloudConnectionIfActive(Mode? mode)
        => _providerFactory.PrewarmCloudConnectionIfActive(mode);

    /// <summary>
    /// Starts the foreground keepalive ticker. Call from
    /// <c>MainWindow.Activated</c>; pair with <see cref="StopKeepalive"/> on
    /// <c>MainWindow.Deactivated</c> so we don't keep pinging while the app is
    /// in the background.
    /// </summary>
    public void StartKeepalive(Func<Mode?> selectedModeGetter)
    {
        if (selectedModeGetter == null) throw new ArgumentNullException(nameof(selectedModeGetter));
        _keepaliveModeGetter = selectedModeGetter;
        _keepaliveStopped = false;
        if (_keepaliveTimer != null) return;

        var timer = new System.Timers.Timer(KeepaliveInterval.TotalMilliseconds)
        {
            AutoReset = true
        };
        timer.Elapsed += OnKeepaliveTick;
        _keepaliveTimer = timer;
        timer.Start();
        LoggingService.Debug("TranscriptionOrchestrator: Keepalive started");
    }

    /// <summary>
    /// Stops the keepalive ticker. Idempotent.
    /// </summary>
    public void StopKeepalive()
    {
        _keepaliveStopped = true;

        var timer = _keepaliveTimer;
        if (timer == null) return;
        _keepaliveTimer = null;

        try
        {
            timer.Stop();
            timer.Elapsed -= OnKeepaliveTick;
            timer.Dispose();
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"TranscriptionOrchestrator: Keepalive stop failed: {ex.Message}");
        }
        LoggingService.Debug("TranscriptionOrchestrator: Keepalive stopped");
    }

    private void OnKeepaliveTick(object? sender, System.Timers.ElapsedEventArgs e)
    {
        // Stop()/Dispose() on System.Timers.Timer doesn't cancel an
        // already-queued Elapsed callback, so drop late ticks here.
        if (_keepaliveStopped || _disposed) return;
        try
        {
            var mode = _keepaliveModeGetter?.Invoke();
            _providerFactory.PrewarmCloudConnectionIfActiveForced(mode);
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"TranscriptionOrchestrator: Keepalive tick failed: {ex.Message}");
        }
    }

    // =========================================================================
    // MAIN API
    // =========================================================================

    /// <summary>
    /// Performs transcription using the appropriate provider based on mode settings.
    /// Handles post-processing and vocabulary replacements.
    /// </summary>
    /// <param name="audioPath">Path to audio file.</param>
    /// <param name="mode">Mode containing provider and post-processing settings.</param>
    /// <param name="vocabulary">Custom vocabulary terms for accuracy boosting.</param>
    /// <param name="localTranscriptionProvider">Local transcription provider for local modes (must be available).</param>
    /// <param name="applicationContext">Optional application context for prompt enrichment.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>TranscriptionResult with raw text, final text, and provider info.</returns>
    /// <exception cref="TranscriptionException">On transcription failure.</exception>
    /// <exception cref="InvalidOperationException">If local mode but service not initialized.</exception>
    public async Task<TranscriptionResult> TranscribeAsync(
        string audioPath,
        Mode mode,
        IReadOnlyList<string>? vocabulary = null,
        ITranscriptionProvider? localTranscriptionProvider = null,
        ApplicationContext? applicationContext = null,
        CancellationToken cancellationToken = default,
        TranscriptionCallSite callSite = TranscriptionCallSite.Gui,
        bool applyPostProcessing = true)
    {
        // Guard clauses
        if (string.IsNullOrEmpty(audioPath))
            throw new ArgumentException("Audio path cannot be empty", nameof(audioPath));
        if (mode == null)
            throw new ArgumentNullException(nameof(mode));

        // Stamp the call site on the async flow so the bridged
        // post-processing warning carries it to the GUI subscriber.
        _currentCallSite.Value = callSite;

        LoggingService.Info($"TranscriptionOrchestrator: Starting transcription (provider={mode.ProviderType}, mode={mode.Name}, callSite={callSite})");

        // Determine language (null for auto-detect)
        string? language = mode.Language == "auto" ? null : mode.Language;

        // STEP 1: Perform transcription (cloud or local)
        string rawText;
        string transcriptionProvider;
        TranscriptionProviderDiagnostics? diagnostics = null;

        if (mode.ProviderType == "cloud")
        {
            (rawText, transcriptionProvider, diagnostics) = await TranscribeCloudAsync(audioPath, mode, language, vocabulary, cancellationToken);
        }
        else
        {
            (rawText, transcriptionProvider) = await TranscribeLocalAsync(audioPath, language, localTranscriptionProvider, cancellationToken);
        }

        // STEP 2: Check for empty result
        if (string.IsNullOrWhiteSpace(rawText))
        {
            LoggingService.Warn("TranscriptionOrchestrator: Empty transcription result");
            throw new TranscriptionException(
                TranscriptionErrorCode.NoSpeechDetected,
                "No speech detected in audio",
                transcriptionProvider,
                providerDiagnostics: diagnostics);
        }

        // STEP 3: Post-processing (if enabled)
        string finalText = rawText;
        string? postProcessedText = null;
        string? postProcessingProvider = null;

        if (applyPostProcessing && mode.PostProcessingMode != 0)
        {
            LoggingService.Info("TranscriptionOrchestrator: Starting post-processing");
            var postProcessingResult = await _postProcessingService.ProcessAsync(rawText, mode, applicationContext, cancellationToken);
            // Apply vocabulary replacements whether or not AI post-processing succeeded.
            // Matches macOS + v1.6: when post-processing fails/skips, ProcessAsync returns the
            // original transcription, and the user's vocabulary corrections must still be applied.
            // (ApplyReplacements no-ops on empty input / when no vocabulary is configured.)
            finalText = _vocabularyProcessor.ApplyReplacements(postProcessingResult.Text);
            if (postProcessingResult.WasApplied)
            {
                postProcessedText = finalText; // Store final processed text
                postProcessingProvider = mode.PostProcessingProvider;
                LoggingService.Info($"TranscriptionOrchestrator: Post-processing complete ({rawText.Length} -> {finalText.Length} chars)");
            }
            else
            {
                LoggingService.Info($"TranscriptionOrchestrator: Post-processing skipped or failed; applied vocabulary to original transcription ({rawText.Length} -> {finalText.Length} chars)");
            }
        }
        else if (applyPostProcessing)
        {
            // No AI post-processing — optionally apply lightweight filler word removal.
            if (SettingsService.Instance.RemoveFillerWords)
            {
                finalText = SmartSpacing.RemoveFillerWords(finalText);
            }
        }

        return new TranscriptionResult(
            RawText: rawText,
            FinalText: finalText,
            TranscriptionProvider: transcriptionProvider,
            PostProcessingProvider: postProcessingProvider,
            PostProcessedText: postProcessedText,
            Diagnostics: diagnostics);
    }

    /// <summary>
    /// Simplified overload for retry operations that don't need vocabulary.
    /// </summary>
    public Task<TranscriptionResult> TranscribeAsync(
        string audioPath,
        Mode mode,
        ITranscriptionProvider? localTranscriptionProvider = null,
        ApplicationContext? applicationContext = null,
        CancellationToken cancellationToken = default,
        TranscriptionCallSite callSite = TranscriptionCallSite.Gui,
        bool applyPostProcessing = true)
    {
        return TranscribeAsync(audioPath, mode, vocabulary: null, localTranscriptionProvider, applicationContext, cancellationToken, callSite, applyPostProcessing);
    }

    // =========================================================================
    // CLOUD TRANSCRIPTION
    // =========================================================================

    private async Task<(string text, string provider, TranscriptionProviderDiagnostics? diagnostics)> TranscribeCloudAsync(
        string audioPath,
        Mode mode,
        string? language,
        IReadOnlyList<string>? vocabulary,
        CancellationToken cancellationToken)
    {
        var providerType = CloudTranscriptionProviderExtensions.FromIdentifier(mode.CloudProvider);

        LoggingService.LogPerformanceMarker("TranscriptionOrchestrator", $"Cloud transcription via {providerType}");

        // Get configured provider (validates API key, configures model)
        var provider = _providerFactory.GetConfiguredCloudProvider(providerType, mode.CloudTranscriptionModel);

        // Set Gemini custom prompt if applicable
        if (providerType == CloudTranscriptionProvider.Gemini && provider is GeminiTranscriptionService geminiService)
        {
            geminiService.SetCustomPrompt(mode.GeminiCustomPrompt);
        }

        // Perform transcription
        // Note: Mistral doesn't support vocabulary, handled in the service
        var effectiveVocabulary = providerType.SupportsVocabulary() ? vocabulary : null;

        string result;
        if (providerType == CloudTranscriptionProvider.HyperWhisperCloud && provider is HyperWhisperCloudService hwCloud)
        {
            // HyperWhisper Cloud supports accuracy tier + per-provider model + domain selection
            result = await hwCloud.TranscribeAsync(audioPath, language, effectiveVocabulary,
                mode.CloudAccuracyTier, mode.CloudTranscriptionModel, mode.CloudTranscriptionDomain, cancellationToken);
        }
        else
        {
            result = await provider.TranscribeAsync(audioPath, language, effectiveVocabulary, cancellationToken);
        }

        var displayName = TranscriptionProviderFactory.GetProviderDisplayName(providerType, mode.CloudTranscriptionModel);
        var diagnostics = (provider as ITranscriptionDiagnosticsSource)?.LastDiagnostics;
        return (result, displayName, diagnostics);
    }

    // =========================================================================
    // LOCAL TRANSCRIPTION
    // =========================================================================

    private async Task<(string text, string provider)> TranscribeLocalAsync(
        string audioPath,
        string? language,
        ITranscriptionProvider? provider,
        CancellationToken cancellationToken)
    {
        // Validate local provider is provided and available
        if (provider == null)
        {
            throw new InvalidOperationException(
                "ITranscriptionProvider must be provided for local transcription");
        }

        if (!provider.IsAvailable)
        {
            throw new TranscriptionException(
                TranscriptionErrorCode.ModelNotLoaded,
                "Local transcription model not loaded",
                "Local");
        }

        LoggingService.LogPerformanceMarker("TranscriptionOrchestrator", "Local transcription");

        var result = await provider.TranscribeAsync(audioPath, language, cancellationToken: cancellationToken);
        return (result, provider.Name);
    }

    // =========================================================================
    // DISPOSAL
    // =========================================================================

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopKeepalive();

        _postProcessingService.WarningOccurred -= OnPostProcessingWarning;
        _providerFactory.Dispose();
        _postProcessingService.Dispose();

        LoggingService.Info("TranscriptionOrchestrator: Disposed");
        GC.SuppressFinalize(this);
    }
}
