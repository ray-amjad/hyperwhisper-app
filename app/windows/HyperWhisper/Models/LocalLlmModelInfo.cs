namespace HyperWhisper.Models;

/// <summary>
/// Metadata for downloadable local LLM GGUF models used by offline post-processing.
/// IDs intentionally match the macOS local LLM catalog and LanguageModelInfo IDs.
/// </summary>
public class LocalLlmModelInfo
{
    public string Id { get; }
    public string DisplayName { get; }
    public string FileName { get; }
    public string HuggingFaceRepo { get; }
    public string HuggingFaceFile { get; }
    public string Size { get; }
    public long SizeInBytes { get; }
    public long RecommendedVramBytes { get; }
    public string Description { get; }
    public bool IsRecommended { get; }

    public string DownloadUrl => $"https://huggingface.co/{HuggingFaceRepo}/resolve/main/{HuggingFaceFile}";

    public double RecommendedVramGB => RecommendedVramBytes / (1024.0 * 1024.0 * 1024.0);

    public string RecommendedVramDisplay => $"~{RecommendedVramGB:F0} GB";

    public LocalLlmModelInfo(
        string id,
        string displayName,
        string fileName,
        string huggingFaceRepo,
        string huggingFaceFile,
        string size,
        long sizeInBytes,
        long recommendedVramBytes,
        string description,
        bool isRecommended)
    {
        Id = id;
        DisplayName = displayName;
        FileName = fileName;
        HuggingFaceRepo = huggingFaceRepo;
        HuggingFaceFile = huggingFaceFile;
        Size = size;
        SizeInBytes = sizeInBytes;
        RecommendedVramBytes = recommendedVramBytes;
        Description = description;
        IsRecommended = isRecommended;
    }

    public override string ToString() => $"{DisplayName} ({Size})";

    public static readonly LocalLlmModelInfo[] AllModels =
    [
        new(
            id: "gemma-4-E2B-it-Q4_K_M.gguf",
            displayName: "Gemma 4 E2B (Recommended)",
            fileName: "gemma-4-E2B-it-Q4_K_M.gguf",
            huggingFaceRepo: "unsloth/gemma-4-E2B-it-GGUF",
            huggingFaceFile: "gemma-4-E2B-it-Q4_K_M.gguf",
            size: "3.1 GB",
            sizeInBytes: 3_100_000_000,
            recommendedVramBytes: 4L * 1024 * 1024 * 1024,
            description: "Fast and accurate, good all-rounder for local text cleanup.",
            isRecommended: true),

        new(
            id: "gemma-4-E4B-it-Q4_K_M.gguf",
            displayName: "Gemma 4 E4B",
            fileName: "gemma-4-E4B-it-Q4_K_M.gguf",
            huggingFaceRepo: "unsloth/gemma-4-E4B-it-GGUF",
            huggingFaceFile: "gemma-4-E4B-it-Q4_K_M.gguf",
            size: "5 GB",
            sizeInBytes: 5_000_000_000,
            recommendedVramBytes: 6L * 1024 * 1024 * 1024,
            description: "Balanced local model with higher quality and more detail.",
            isRecommended: false),

        new(
            id: "gemma-4-26B-A4B-it-UD-Q4_K_M.gguf",
            displayName: "Gemma 4 26B MoE",
            fileName: "gemma-4-26B-A4B-it-UD-Q4_K_M.gguf",
            huggingFaceRepo: "unsloth/gemma-4-26B-A4B-it-GGUF",
            huggingFaceFile: "gemma-4-26B-A4B-it-UD-Q4_K_M.gguf",
            size: "16.9 GB",
            sizeInBytes: 16_900_000_000,
            recommendedVramBytes: 18L * 1024 * 1024 * 1024,
            description: "Higher quality mixture-of-experts model for capable systems.",
            isRecommended: false),

        new(
            id: "gemma-4-31B-it-Q4_K_M.gguf",
            displayName: "Gemma 4 31B Dense",
            fileName: "gemma-4-31B-it-Q4_K_M.gguf",
            huggingFaceRepo: "unsloth/gemma-4-31B-it-GGUF",
            huggingFaceFile: "gemma-4-31B-it-Q4_K_M.gguf",
            size: "18.3 GB",
            sizeInBytes: 18_300_000_000,
            recommendedVramBytes: 20L * 1024 * 1024 * 1024,
            description: "Highest quality dense local model, intended for high-memory machines.",
            isRecommended: false)
    ];

    public static LocalLlmModelInfo[] GetAll() => AllModels;

    public static LocalLlmModelInfo GetDefault() => AllModels.First(m => m.IsRecommended);

    public static LocalLlmModelInfo? GetById(string? id) =>
        string.IsNullOrEmpty(id) ? null : AllModels.FirstOrDefault(m => m.Id == id);
}
