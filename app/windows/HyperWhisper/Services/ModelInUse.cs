using HyperWhisper.Data.Entities;
using HyperWhisper.Models;

namespace HyperWhisper.Services;

/// <summary>
/// Which mode still uses a local model, so the Model Library can refuse to
/// delete it (#2245). A deleted model a mode still points at breaks that mode:
/// its transcription or its AI rewrite fails on the next recording.
/// <see cref="Views.Pages.Settings.ModelsSettingsPage"/> asks these before it
/// deletes; the smoke suite drives them with hand-built modes.
/// </summary>
public static class ModelInUse
{
    public static Mode? FindModeUsingWhisperModel(IEnumerable<Mode> modes, string modelId) =>
        modes.FirstOrDefault(m => m.ProviderType != "cloud"
            && string.Equals(m.LocalEngine, "whisper", StringComparison.OrdinalIgnoreCase)
            && (string.Equals(m.Model, modelId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(m.ModelType, modelId, StringComparison.OrdinalIgnoreCase)));

    public static Mode? FindModeUsingParakeetModel(IEnumerable<Mode> modes, string modelId) =>
        modes.FirstOrDefault(m => m.ProviderType != "cloud"
            && string.Equals(m.LocalEngine, "parakeet", StringComparison.OrdinalIgnoreCase)
            && string.Equals(m.LocalParakeetModel, modelId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A local-LLM mode stores its GGUF id in <see cref="Mode.LocalPostProcessingModel"/>;
    /// older modes kept it in the legacy <see cref="Mode.LanguageModel"/> field. Both
    /// ids go through <see cref="LanguageModelInfo.MigrateModelId"/>, so a mode on a
    /// retired id still protects the model it migrates to.
    /// </summary>
    public static Mode? FindModeUsingLocalLlm(IEnumerable<Mode> modes, string modelId)
    {
        var targetModelId = LanguageModelInfo.MigrateModelId(modelId);
        return modes.FirstOrDefault(m => string.Equals(m.PostProcessingProvider, PostProcessingProvider.LocalLlm.ToStringValue(), StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                LanguageModelInfo.MigrateModelId(m.LocalPostProcessingModel ?? m.LanguageModel),
                targetModelId,
                StringComparison.OrdinalIgnoreCase));
    }
}
