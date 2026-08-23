namespace HyperWhisper.ModelManagement;

public enum ManagedModelKind { Whisper, Parakeet, LocalLlm }
public enum ManagedModelLayout { SingleFile, FixedFiles, HuggingFaceTree }

public sealed record ModelArtifact(
    string RelativePath,
    Uri DownloadUri,
    long? ExactSizeBytes = null,
    string? Sha256 = null);

public sealed record ManagedModel(
    string Id,
    string DisplayName,
    ManagedModelKind Kind,
    ManagedModelLayout Layout,
    string StorageName,
    long ApproximateSizeBytes,
    bool IsEnglishOnly,
    IReadOnlyList<string> SupportedLanguages,
    IReadOnlyList<ModelArtifact> Artifacts,
    string? HuggingFaceRepository = null,
    string? Description = null,
    long? RecommendedVramBytes = null,
    bool IsRecommended = false,
    bool SupportsStreaming = false);

/// <summary>Authoritative portable copy of the Windows local-model registries.</summary>
public static class PortableModelCatalog
{
    private const string WhisperRoot = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/";

    public static IReadOnlyList<ManagedModel> Whisper { get; } =
    [
        WhisperModel("tiny", "Tiny", 77_691_713, false, 1),
        WhisperModel("tiny.en", "Tiny (English)", 77_704_715, true, 1),
        WhisperModel("base", "Base", 147_951_465, false, 1),
        WhisperModel("base.en", "Base (English)", 147_964_211, true, 1),
        WhisperModel("small", "Small", 487_601_967, false, 2),
        WhisperModel("small.en", "Small (English)", 487_614_201, true, 2),
        WhisperModel("medium", "Medium", 1_533_763_059, false, 5),
        WhisperModel("medium.en", "Medium (English)", 1_533_774_781, true, 5),
        WhisperModel("large-v3-turbo", "Large v3 Turbo", 1_624_555_275, false, 6),
        WhisperModel("large-v2", "Large v2", 3_094_623_691, false, 10),
        WhisperModel("large-v3", "Large v3", 3_095_033_483, false, 10),
    ];

    public static IReadOnlyList<ManagedModel> Parakeet { get; } =
    [
        FixedAsr("parakeet-v2", "Parakeet v2 (English)", 661_000_000, true, ["en"],
            "csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8", supportsStreaming: true),
        FixedAsr("parakeet-v3", "Parakeet v3 (Multilingual)", 671_000_000, false,
            ["en", "de", "es", "fr", "it", "pt", "nl", "pl", "ro", "sv", "da", "fi", "no", "cs", "sk", "hu", "hr", "sl", "bg", "uk", "el", "lt", "lv", "et", "ca", "eu"],
            "csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8", supportsStreaming: true),
        TreeAsr("qwen3-asr-0.6b", "Qwen3 ASR 0.6B", 985_000_000,
            ["ja", "en", "zh", "ko", "es", "fr", "de", "it", "pt", "ru", "ar"],
            "csukuangfj2/sherpa-onnx-qwen3-asr-0.6B-int8-2026-03-25"),
        FixedAsr("nemotron-3.5-ml-560ms", "Nemotron 3.5 Streaming (Multilingual)", 682_000_000, false,
            [
                "en-US", "en-GB", "es-US", "es-ES", "fr-FR", "fr-CA", "it-IT", "pt-BR", "pt-PT",
                "nl-NL", "de-DE", "tr-TR", "ru-RU", "ar-AR", "hi-IN", "ja-JP", "ko-KR", "vi-VN",
                "uk-UA", "pl-PL", "sv-SE", "cs-CZ", "nb-NO", "da-DK", "bg-BG", "fi-FI", "hr-HR",
                "sk-SK", "zh-CN", "hu-HU", "ro-RO", "et-EE",
            ],
            "csukuangfj2/sherpa-onnx-nemotron-3.5-asr-streaming-0.6b-560ms-int8-2026-06-11",
            supportsStreaming: true),
    ];

    public static IReadOnlyList<ManagedModel> LocalLlm { get; } =
    [
        Llm("gemma-4-E2B-it-Q4_K_M.gguf", "Gemma 4 E2B (Recommended)", "unsloth/gemma-4-E2B-it-GGUF", 3_100_000_000, 4, "Fast and accurate, good all-rounder for local text cleanup.", true),
        Llm("gemma-4-E4B-it-Q4_K_M.gguf", "Gemma 4 E4B", "unsloth/gemma-4-E4B-it-GGUF", 5_000_000_000, 6, "Balanced local model with higher quality and more detail.", false),
        Llm("gemma-4-26B-A4B-it-UD-Q4_K_M.gguf", "Gemma 4 26B MoE", "unsloth/gemma-4-26B-A4B-it-GGUF", 16_900_000_000, 18, "Higher quality mixture-of-experts model for capable systems.", false),
        Llm("gemma-4-31B-it-Q4_K_M.gguf", "Gemma 4 31B Dense", "unsloth/gemma-4-31B-it-GGUF", 18_300_000_000, 20, "Highest quality dense local model, intended for high-memory machines.", false),
    ];

    public static IReadOnlyList<ManagedModel> All { get; } = [.. Whisper, .. Parakeet, .. LocalLlm];

    private static ManagedModel WhisperModel(string id, string name, long bytes, bool englishOnly, long vramGb)
    {
        var file = $"ggml-{id}.bin";
        return new(id, name, ManagedModelKind.Whisper, ManagedModelLayout.SingleFile, file, bytes,
            englishOnly, englishOnly ? ["en"] : [], [new(file, new Uri(WhisperRoot + file), bytes)],
            RecommendedVramBytes: vramGb * 1024 * 1024 * 1024);
    }

    private static ManagedModel FixedAsr(string id, string name, long bytes, bool englishOnly,
        IReadOnlyList<string> languages, string repo, bool supportsStreaming = false)
    {
        string[] files = ["encoder.int8.onnx", "decoder.int8.onnx", "joiner.int8.onnx", "tokens.txt"];
        return new(id, name, ManagedModelKind.Parakeet, ManagedModelLayout.FixedFiles, id, bytes,
            englishOnly, languages, files.Select(file => Artifact(repo, file)).ToArray(), repo,
            SupportsStreaming: supportsStreaming);
    }

    private static ManagedModel TreeAsr(string id, string name, long bytes,
        IReadOnlyList<string> languages, string repo) =>
        new(id, name, ManagedModelKind.Parakeet, ManagedModelLayout.HuggingFaceTree, id, bytes,
            false, languages, [], repo);

    private static ManagedModel Llm(string file, string name, string repo, long bytes, long vramGb,
        string description, bool recommended) =>
        new(file, name, ManagedModelKind.LocalLlm, ManagedModelLayout.SingleFile, file, bytes, false, [],
            [Artifact(repo, file)], repo, description, vramGb * 1024 * 1024 * 1024, recommended);

    private static ModelArtifact Artifact(string repo, string file) =>
        new(file, new Uri($"https://huggingface.co/{repo}/resolve/main/{file}"));
}
