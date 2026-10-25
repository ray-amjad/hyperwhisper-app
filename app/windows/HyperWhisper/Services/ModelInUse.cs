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
/// on a model through a fallback still protects it. Two paths load a local
/// transcription model and they read a mode slightly differently: the app's
/// recording path (TranscriptionRetryHandler) and the Local API
/// (WindowsLocalApiBackend). A mode protects every model either path would load.
/// </summary>
public static class ModelInUse
{
    public static Mode? FindModeUsingWhisperModel(IEnumerable<Mode> modes, string modelId) =>
        modes.FirstOrDefault(m => !LocalModeModel.IsCloud(m) && WhisperModelsRun(m).Contains(modelId));

    public static Mode? FindModeUsingParakeetModel(IEnumerable<Mode> modes, string modelId) =>
        modes.FirstOrDefault(m => !LocalModeModel.IsCloud(m) && ParakeetModelsRun(m).Contains(modelId));

    /// <summary>
    /// The GGUF a local-LLM mode's AI rewrite loads: <see cref="PostProcessingService"/>
    /// resolves the stored id (or the default local model) and the local runtime
    /// loads it with <see cref="LocalLlmModelInfo.GetByIdOrDefault"/>. A mode with
    /// no id, an empty one, or one the catalog does not know runs on the default.
    /// A mode with AI rewrite off (<see cref="Mode.PostProcessingMode"/> 0) runs none.
    /// </summary>
    public static Mode? FindModeUsingLocalLlm(IEnumerable<Mode> modes, string modelId) =>
        modes.FirstOrDefault(m => m.PostProcessingMode != 0
            && PostProcessingProviderExtensions.FromString(m.PostProcessingProvider) == PostProcessingProvider.LocalLlm
            && LocalLlmModelInfo.GetByIdOrDefault(PostProcessingService.ResolveModel(
                PostProcessingProvider.LocalLlm,
                PostProcessingService.SelectedModelId(m, PostProcessingProvider.LocalLlm))?.Id).Id == modelId);

    /// <summary>
    /// The Whisper types a local mode can load, matched exactly as the loaders do.
    /// The app runs Whisper unless the engine is exactly "parakeet" and loads
    /// <see cref="Mode.ModelType"/>; a row that keeps it only in the legacy
    /// <see cref="Mode.Model"/> gets it copied into ModelType on the next start
    /// (ModeService.HealMissingModelTypes). The Local API runs Whisper unless
    /// <see cref="LocalModeModel.IsParakeetEngine"/>, and falls back to Model when
    /// ModelType is blank.
    /// </summary>
    private static IEnumerable<string?> WhisperModelsRun(Mode mode)
    {
        if (mode.LocalEngine != "parakeet")
        {
            yield return mode.ModelType ?? (mode.Model != LocalModeModel.CloudModel ? mode.Model : null);
        }

        if (!LocalModeModel.IsParakeetEngine(mode))
        {
            yield return string.IsNullOrWhiteSpace(mode.ModelType) ? mode.Model : mode.ModelType;
        }
    }

    /// <summary>
    /// The sherpa-onnx ids a local mode can load, matched exactly as the loaders
    /// do. The app takes that engine only for an exact "parakeet" and loads
    /// <see cref="Mode.LocalParakeetModel"/>; the Local API takes it for any case
    /// of "parakeet", and falls back to <see cref="Mode.Model"/> when the id is blank.
    /// </summary>
    private static IEnumerable<string?> ParakeetModelsRun(Mode mode)
    {
        if (mode.LocalEngine == "parakeet")
        {
            yield return mode.LocalParakeetModel;
        }

        if (LocalModeModel.IsParakeetEngine(mode))
        {
            yield return string.IsNullOrWhiteSpace(mode.LocalParakeetModel) ? mode.Model : mode.LocalParakeetModel;
        }
    }
}
