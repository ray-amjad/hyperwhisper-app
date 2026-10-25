using HyperWhisper.Data.Entities;
using HyperWhisper.Models;

namespace HyperWhisper.Services;

/// <summary>
/// Which mode still uses a local model, so the Model Library can refuse to
/// delete it (#2245). A deleted model a mode still points at breaks that mode:
/// its transcription or its AI rewrite fails on the next recording.
/// <see cref="Views.Pages.Settings.ModelsSettingsPage"/> asks these before it
/// deletes; the smoke suite drives them with hand-built modes.
///
/// Each check resolves the mode's model the way the runtime does, with the same
/// lookup, the same case rules and the same default (#2536), so a mode that runs
/// on a model through a fallback still protects it.
/// </summary>
public static class ModelInUse
{
    public static Mode? FindModeUsingWhisperModel(IEnumerable<Mode> modes, string modelId) =>
        modes.FirstOrDefault(m => RunsLocally(m) && !RunsParakeet(m) && WhisperModelType(m) == modelId);

    public static Mode? FindModeUsingParakeetModel(IEnumerable<Mode> modes, string modelId) =>
        modes.FirstOrDefault(m => RunsLocally(m) && RunsParakeet(m) && m.LocalParakeetModel == modelId);

    /// <summary>
    /// The GGUF a local-LLM mode's AI rewrite loads: <see cref="PostProcessingService"/>
    /// resolves the stored id (or the default local model) and the local runtime
    /// loads it with <see cref="LocalLlmModelInfo.GetByIdOrDefault"/>. A mode with
    /// no id, an empty one, or one the catalog does not know runs on the default.
    /// </summary>
    public static Mode? FindModeUsingLocalLlm(IEnumerable<Mode> modes, string modelId) =>
        modes.FirstOrDefault(m => PostProcessingProviderExtensions.FromString(m.PostProcessingProvider) == PostProcessingProvider.LocalLlm
            && LocalLlmModelInfo.GetByIdOrDefault(PostProcessingService.ResolveModel(
                PostProcessingProvider.LocalLlm,
                PostProcessingService.SelectedModelId(m, PostProcessingProvider.LocalLlm))?.Id).Id == modelId);

    // TranscriptionOrchestrator: "cloud" goes to the cloud, everything else runs here.
    private static bool RunsLocally(Mode mode) => !LocalModeModel.IsCloud(mode);

    // TranscriptionRetryHandler.EnsureLocalModelLoadedAsync: only an exact
    // "parakeet" takes the sherpa-onnx engine; any other value runs Whisper.
    private static bool RunsParakeet(Mode mode) => mode.LocalEngine == "parakeet";

    /// <summary>
    /// The Whisper type the runtime loads (<see cref="Mode.ModelType"/>, matched
    /// exactly). A row that still keeps it only in the legacy <see cref="Mode.Model"/>
    /// gets it copied into ModelType on the next start (ModeService.HealMissingModelTypes).
    /// </summary>
    private static string? WhisperModelType(Mode mode) =>
        mode.ModelType ?? (mode.Model is { } legacy && legacy != LocalModeModel.CloudModel ? legacy : null);
}
