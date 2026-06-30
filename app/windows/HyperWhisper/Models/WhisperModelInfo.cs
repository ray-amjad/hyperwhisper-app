namespace HyperWhisper.Models;

/// <summary>
/// WHISPER MODEL METADATA
///
/// Purpose:
/// Represents a Whisper GGML model available for download and use.
/// Contains metadata for display in the UI and download management.
///
/// MODEL FORMAT:
/// All models use GGML binary format (.bin files).
/// Downloaded from Hugging Face: https://huggingface.co/ggerganov/whisper.cpp/tree/main
///
/// VRAM REQUIREMENTS:
/// Each model has a recommended minimum VRAM for GPU acceleration.
/// If the user's GPU has less VRAM than required, transcription will be slow
/// due to memory paging between GPU and system RAM.
///
/// VRAM estimates based on:
/// - Model file size (weights)
/// - Runtime memory overhead (activations, KV cache)
/// - Approximately 2-3x the model file size for full GPU operation
///
/// NOTE: This file was rewritten to remove dependency on Whisper.net.
/// The old version used Whisper.net.Ggml.GgmlType enum.
/// </summary>
public class WhisperModelInfo
{
    /// <summary>
    /// Model type identifier (e.g., "tiny", "base", "medium").
    /// Used to construct the filename: ggml-{Type}.bin
    /// </summary>
    public string Type { get; }

    /// <summary>
    /// Human-readable display name for the UI.
    /// </summary>
    public string DisplayName { get; }

    /// <summary>
    /// Approximate download size (for user information).
    /// </summary>
    public string Size { get; }

    /// <summary>
    /// Whether this is an English-only model (faster but only supports English).
    /// </summary>
    public bool IsEnglishOnly { get; }

    /// <summary>
    /// Approximate size in bytes (for progress calculation during download).
    /// </summary>
    public long SizeInBytes { get; }

    /// <summary>
    /// Recommended minimum VRAM in bytes for GPU acceleration.
    ///
    /// GPU MEMORY USAGE:
    /// The model requires approximately 2-3x the file size in VRAM:
    /// - Model weights (equal to file size)
    /// - Activation memory (depends on audio length, typically 0.5-1x)
    /// - KV cache and buffers (0.5-1x)
    ///
    /// If the GPU has less VRAM than this value, transcription will be slow
    /// due to memory paging between GPU VRAM and system RAM.
    /// </summary>
    public long RecommendedVramBytes { get; }

    /// <summary>
    /// Recommended VRAM in gigabytes.
    /// </summary>
    public double RecommendedVramGB => RecommendedVramBytes / (1024.0 * 1024.0 * 1024.0);

    /// <summary>
    /// Human-readable VRAM requirement string (e.g., "~2 GB").
    /// </summary>
    public string RecommendedVramDisplay => $"~{RecommendedVramGB:F0} GB";

    public WhisperModelInfo(string type, string displayName, string size, bool isEnglishOnly, long sizeInBytes, long recommendedVramBytes)
    {
        Type = type;
        DisplayName = displayName;
        Size = size;
        IsEnglishOnly = isEnglishOnly;
        SizeInBytes = sizeInBytes;
        RecommendedVramBytes = recommendedVramBytes;
    }

    public override string ToString() => $"{DisplayName} ({Size})";

    /// <summary>
    /// All available Whisper models for download.
    ///
    /// DOWNLOAD URLS:
    /// Base URL: https://huggingface.co/ggerganov/whisper.cpp/resolve/main/
    /// Filename pattern: ggml-{type}.bin
    ///
    /// Example: ggml-base.bin, ggml-medium.en.bin
    ///
    /// VRAM REQUIREMENTS (recommended minimum for full GPU acceleration):
    /// Based on OpenAI documentation and community benchmarks:
    /// - Tiny/Base: ~1 GB (works on any GPU, 39-74M params, 78-148 MB files)
    /// - Small: ~2 GB (244M params, 488 MB file)
    /// - Medium: ~5 GB (769M params, 1.5 GB file)
    /// - Large v3 Turbo: ~6 GB (809M params, 1.5 GB file, optimized for speed)
    /// - Large v2/v3: ~10 GB (1550M params, 3.1 GB files, requires RTX 3080+)
    ///
    /// These values are conservative estimates. The actual VRAM usage depends on:
    /// - Audio length (longer audio = more activation memory)
    /// - Batch size (we use batch size 1)
    /// - DirectCompute implementation overhead
    /// </summary>
    public static readonly WhisperModelInfo[] AllModels = new[]
    {
        // Tiny models - fastest, lowest accuracy (~1 GB VRAM, 39M parameters)
        new WhisperModelInfo("tiny", "Tiny", "78 MB", false, 77_691_713, 1L * 1024 * 1024 * 1024),
        new WhisperModelInfo("tiny.en", "Tiny (English)", "78 MB", true, 77_704_715, 1L * 1024 * 1024 * 1024),

        // Base models - good balance for quick transcription (~1 GB VRAM, 74M parameters)
        new WhisperModelInfo("base", "Base", "148 MB", false, 147_951_465, 1L * 1024 * 1024 * 1024),
        new WhisperModelInfo("base.en", "Base (English)", "148 MB", true, 147_964_211, 1L * 1024 * 1024 * 1024),

        // Small models - better accuracy, moderate speed (~2 GB VRAM, 244M parameters)
        new WhisperModelInfo("small", "Small", "488 MB", false, 487_601_967, 2L * 1024 * 1024 * 1024),
        new WhisperModelInfo("small.en", "Small (English)", "488 MB", true, 487_614_201, 2L * 1024 * 1024 * 1024),

        // Medium models - high accuracy, slower (~5 GB VRAM, 769M parameters)
        new WhisperModelInfo("medium", "Medium", "1.5 GB", false, 1_533_763_059, 5L * 1024 * 1024 * 1024),
        new WhisperModelInfo("medium.en", "Medium (English)", "1.5 GB", true, 1_533_774_781, 5L * 1024 * 1024 * 1024),

        // Large v3 Turbo - 809M parameters, optimized for speed (~6 GB VRAM, much faster than full Large)
        new WhisperModelInfo("large-v3-turbo", "Large v3 Turbo", "1.5 GB", false, 1_624_555_275, 6L * 1024 * 1024 * 1024),

        // Large models - highest accuracy, slowest (~10 GB VRAM - requires RTX 3080+ or similar)
        new WhisperModelInfo("large-v2", "Large v2", "3.1 GB", false, 3_094_623_691, 10L * 1024 * 1024 * 1024),
        new WhisperModelInfo("large-v3", "Large v3", "3.1 GB", false, 3_095_033_483, 10L * 1024 * 1024 * 1024),
    };

    /// <summary>
    /// Checks if a GPU has enough VRAM for this model.
    /// </summary>
    /// <param name="gpuVramBytes">The GPU's VRAM in bytes.</param>
    /// <returns>True if the GPU has enough VRAM, false otherwise.</returns>
    public bool FitsInVram(long gpuVramBytes)
    {
        return gpuVramBytes >= RecommendedVramBytes;
    }

    /// <summary>
    /// Gets a warning message if the model may be slow on the given GPU.
    /// Returns null if the GPU has sufficient VRAM.
    /// </summary>
    /// <param name="gpuVramBytes">The GPU's VRAM in bytes.</param>
    /// <param name="gpuName">The GPU's name for display.</param>
    /// <returns>Warning message, or null if no warning needed.</returns>
    public string? GetVramWarning(long gpuVramBytes, string gpuName)
    {
        if (FitsInVram(gpuVramBytes))
        {
            return null;
        }

        double gpuVramGB = gpuVramBytes / (1024.0 * 1024.0 * 1024.0);

        return $"Warning: {DisplayName} requires {RecommendedVramDisplay} VRAM, " +
               $"but your {gpuName} only has {gpuVramGB:F1} GB.\n\n" +
               $"This model cannot run properly on your hardware.\n\n" +
               $"Use HyperWhisper Cloud for the best transcription experience on this device.";
    }
}
