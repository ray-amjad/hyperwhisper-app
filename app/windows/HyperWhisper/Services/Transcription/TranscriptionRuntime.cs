using HyperWhisper.Models;
using HyperWhisper.Services;

namespace HyperWhisper.Services.Transcription;

/// <summary>
/// Where a <see cref="TranscriptionOrchestrator.TranscribeAsync"/> call came
/// from. The orchestrator is shared between the GUI and the Local API server;
/// the call site lets event subscribers (e.g. the GUI's toast handler)
/// suppress UI work for API-driven calls so post-processing failures don't
/// pop a toast in the user's main window.
/// </summary>
public enum TranscriptionCallSite
{
    Gui,
    Api
}

/// <summary>
/// Carries the originating call site alongside the toast payload so GUI
/// subscribers to <see cref="TranscriptionOrchestrator.PostProcessingWarning"/>
/// can early-return on API-driven warnings.
/// </summary>
public sealed class OrchestratorPostProcessingWarningEventArgs : ErrorToastEventArgs
{
    public TranscriptionCallSite CallSite { get; }

    public OrchestratorPostProcessingWarningEventArgs(ErrorToastEventArgs source, TranscriptionCallSite callSite)
        : base(
            source.Message,
            showSettingsButton: source.ShowSettingsButton,
            settingsSection: source.SettingsSection,
            guidanceText: source.GuidanceText,
            openApiKeysManager: source.OpenApiKeysManager)
    {
        CallSite = callSite;
    }
}

/// <summary>
/// Process-wide singleton accessor for the shared <see cref="TranscriptionOrchestrator"/>
/// and local-engine <see cref="ITranscriptionProvider"/>. Both the Local API
/// server (constructed in <c>App.OnStartup</c>) and the GUI <c>MainViewModel</c>
/// read from this static so they observe the same loaded Whisper model — fixes
/// the Phase 2 split where the API server had its own un-initialized
/// <see cref="TranscriptionService"/> instance and returned MODEL_NOT_INSTALLED
/// even when the GUI had the model loaded.
///
/// Disposal is deferred to process exit; the OS reclaims the native handles.
/// </summary>
public static class TranscriptionRuntime
{
    public static TranscriptionOrchestrator Orchestrator { get; } = new();
    public static TranscriptionService LocalProvider { get; } = new(isShared: true);
    public static ParakeetTranscriptionService ParakeetProvider { get; } = new(isShared: true);
}
