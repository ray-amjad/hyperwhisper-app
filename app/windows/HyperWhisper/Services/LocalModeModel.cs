using HyperWhisper.Data.Entities;

namespace HyperWhisper.Services;

/// <summary>
/// The model ids a LOCAL (on-device) Windows mode stores and exports (#1477).
///
/// A Windows mode keeps its routing in <see cref="Mode.ProviderType"/> and its
/// local model in <see cref="Mode.ModelType"/> (Whisper) or
/// <see cref="Mode.LocalParakeetModel"/> (the sherpa-onnx engine). The legacy
/// <see cref="Mode.Model"/> column is what crosses to the other heads as the
/// portable <c>model</c>, and macOS routes <c>model == "cloud"</c> (or an empty
/// model) to the cloud, while Linux routes on <c>cloudProvider</c>. The mode
/// editor used to seed <c>Model = "cloud"</c> / <c>CloudProvider = "hyperwhisper"</c>
/// and never rewrite them when the user chose On-device, so a local mode
/// exported as a HyperWhisper Cloud mode.
/// </summary>
public static class LocalModeModel
{
    /// <summary>The macOS id of Parakeet TDT 0.6B v2 (macOS ParakeetModelManager).</summary>
    public const string MacParakeetV2Id = "parakeet-tdt-0.6b-v2";

    /// <summary>The macOS id of Parakeet TDT 0.6B v3 (macOS ParakeetModelManager).</summary>
    public const string MacParakeetV3Id = "parakeet-tdt-0.6b-v3";

    /// <summary>
    /// The Whisper id every head ships ("base" on macOS, Linux and Windows), used
    /// when a local mode names no usable model at all. Never empty and never
    /// "cloud": macOS reads both of those as a cloud mode.
    /// </summary>
    public const string FallbackWhisperModel = "base";

    /// <summary>
    /// Whether this mode transcribes in the cloud. Mirrors the one branch the app
    /// takes, <c>TranscriptionOrchestrator.TranscribeAsync</c>: <c>"cloud"</c> goes
    /// to the cloud and everything else runs on this machine.
    /// </summary>
    public static bool IsCloud(Mode mode) => mode.ProviderType == "cloud";

    /// <summary>Whether the mode's local engine is the sherpa-onnx (Parakeet) engine.</summary>
    public static bool IsParakeetEngine(Mode mode)
        => string.Equals(mode.LocalEngine, "parakeet", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The Windows-side value <see cref="Mode.Model"/> should hold for a local
    /// mode: the Parakeet id for the sherpa-onnx engine, else the Whisper type.
    /// This is the same value the Local API create / PATCH / transcribe paths
    /// already mirror into <c>Model</c>.
    /// </summary>
    public static string StoredLocalModel(Mode mode)
    {
        if (IsParakeetEngine(mode) && IsUsableLocalId(mode.LocalParakeetModel))
        {
            return mode.LocalParakeetModel!.Trim();
        }

        return WhisperModel(mode);
    }

    /// <summary>
    /// The portable <c>model</c> a local mode exports. Parakeet v2 / v3 use the
    /// macOS ids so macOS restores the same engine; any other sherpa-onnx id
    /// (Qwen3 ASR shares its id with macOS) and every Whisper type cross
    /// verbatim. The result is never empty and never "cloud", so every head
    /// reads the mode as local.
    /// </summary>
    public static string PortableLocalModel(Mode mode)
    {
        if (IsParakeetEngine(mode) && IsUsableLocalId(mode.LocalParakeetModel))
        {
            var id = mode.LocalParakeetModel!.Trim();
            if (string.Equals(id, "parakeet-v2", StringComparison.OrdinalIgnoreCase)) return MacParakeetV2Id;
            if (string.Equals(id, "parakeet-v3", StringComparison.OrdinalIgnoreCase)) return MacParakeetV3Id;
            return id;
        }

        return WhisperModel(mode);
    }

    /// <summary>
    /// What choosing On-device in the mode editor writes on top of the engine
    /// columns: <see cref="Mode.Model"/> follows the local model, and the cloud
    /// provider is cleared, so the row stops saying "cloud" anywhere.
    /// </summary>
    public static void ApplyOnDevice(Mode mode)
    {
        mode.Model = StoredLocalModel(mode);
        mode.CloudProvider = null;
    }

    private static string WhisperModel(Mode mode)
    {
        if (IsUsableLocalId(mode.ModelType)) return mode.ModelType!.Trim();
        if (IsUsableLocalId(mode.Model)) return mode.Model!.Trim();
        return FallbackWhisperModel;
    }

    private static bool IsUsableLocalId(string? id)
        => !string.IsNullOrWhiteSpace(id)
           && !string.Equals(id.Trim(), "cloud", StringComparison.OrdinalIgnoreCase);
}
